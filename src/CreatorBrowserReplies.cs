using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

partial class LoginWindow {
    sealed class CreatorBrowserReplies : IDisposable {
        readonly CoreWebView2 core;
        readonly Func<Uri,bool> allowedOrigin;
        readonly JavaScriptSerializer serializer;
        readonly Func<Uri,string,bool> matchRequest;
        readonly Func<Uri,string,bool> matchForm;
        readonly Action<Uri,string> handleReply;
        readonly CoreWebView2DevToolsProtocolEventReceiver requests, responses, finished, failed;
        readonly Dictionary<string,WireRequest> pending=new Dictionary<string,WireRequest>();
        bool disposed;

        sealed class WireEvent {
            public string requestId { get; set; }
            public WireRequest request { get; set; }
            public WireResponse response { get; set; }
        }

        sealed class WireRequest {
            public string url { get; set; }
            public string method { get; set; }
            public string postData { get; set; }
            public Uri Address;
            public bool Succeeded;
        }

        sealed class WireResponse {
            public string url { get; set; }
            public int status { get; set; }
        }

        sealed class WireBody {
            public string body { get; set; }
            public bool base64Encoded { get; set; }
            public string postData { get; set; }
        }

        public CreatorBrowserReplies(CoreWebView2 core,JavaScriptSerializer serializer,Func<Uri,bool> allowedOrigin,Func<Uri,string,bool> matchRequest,
            Func<Uri,string,bool> matchForm,Action<Uri,string> handleReply) {
            this.core=core;
            this.allowedOrigin=allowedOrigin;
            this.serializer=serializer;
            this.matchRequest=matchRequest;
            this.matchForm=matchForm;
            this.handleReply=handleReply;
            requests=core.GetDevToolsProtocolEventReceiver("Network.requestWillBeSent");
            responses=core.GetDevToolsProtocolEventReceiver("Network.responseReceived");
            finished=core.GetDevToolsProtocolEventReceiver("Network.loadingFinished");
            failed=core.GetDevToolsProtocolEventReceiver("Network.loadingFailed");
            requests.DevToolsProtocolEventReceived+=OnRequest;
            responses.DevToolsProtocolEventReceived+=OnResponse;
            finished.DevToolsProtocolEventReceived+=OnFinished;
            failed.DevToolsProtocolEventReceived+=OnFailed;
        }

        public Task Start() {
            return core.CallDevToolsProtocolMethodAsync("Network.enable","{\"maxPostDataSize\":262144}");
        }

        void OnRequest(object sender,CoreWebView2DevToolsProtocolEventReceivedEventArgs args) {
            var item=ParseEvent(args);
            if(item==null || String.IsNullOrEmpty(item.requestId))return;
            pending.Remove(item.requestId);
            Uri address;
            var request=item.request;
            if(request==null || !Uri.TryCreate(request.url,UriKind.Absolute,out address) ||
                !allowedOrigin(address) || !matchRequest(address,request.method))return;
            request.Address=address;
            pending.Add(item.requestId,request);
        }

        void OnResponse(object sender,CoreWebView2DevToolsProtocolEventReceivedEventArgs args) {
            var item=ParseEvent(args);
            WireRequest request;
            if(item==null || item.requestId==null || !pending.TryGetValue(item.requestId,out request))return;
            request.Succeeded=item.response!=null && item.response.status==200 && item.response.url==request.url;
        }

        void OnFailed(object sender,CoreWebView2DevToolsProtocolEventReceivedEventArgs args) {
            var item=ParseEvent(args);
            if(item!=null && item.requestId!=null)pending.Remove(item.requestId);
        }

        async void OnFinished(object sender,CoreWebView2DevToolsProtocolEventReceivedEventArgs args) {
            var item=ParseEvent(args);
            WireRequest request;
            if(item==null || item.requestId==null || !pending.TryGetValue(item.requestId,out request))return;
            pending.Remove(item.requestId);
            if(!request.Succeeded || disposed)return;
            string requestKey=serializer.Serialize(new {requestId=item.requestId});
            try {
                var form=request.postData;
                if(request.method=="POST" && form==null) {
                    string raw=await core.CallDevToolsProtocolMethodAsync("Network.getRequestPostData",requestKey);
                    var data=serializer.Deserialize<WireBody>(raw);
                    form=data==null?null:data.postData;
                }
                if(disposed || !matchForm(request.Address,form))return;
                var reply=serializer.Deserialize<WireBody>(await core.CallDevToolsProtocolMethodAsync("Network.getResponseBody",requestKey));
                if(disposed || reply==null || reply.body==null || reply.body.Length>262144)return;
                var body=reply.base64Encoded?Encoding.UTF8.GetString(Convert.FromBase64String(reply.body)):reply.body;
                handleReply(request.Address,body);
            } catch(ArgumentException) { }
            catch(InvalidOperationException) { }
            catch(IOException) { }
            catch(FormatException) { }
            catch(System.Runtime.InteropServices.COMException) { }
        }

        WireEvent ParseEvent(CoreWebView2DevToolsProtocolEventReceivedEventArgs args) {
            if(disposed)return null;
            try {return serializer.Deserialize<WireEvent>(args.ParameterObjectAsJson);}
            catch(ArgumentException) {return null;}
            catch(InvalidOperationException) {return null;}
        }

        public void Dispose() {
            disposed=true;
            requests.DevToolsProtocolEventReceived-=OnRequest;
            responses.DevToolsProtocolEventReceived-=OnResponse;
            finished.DevToolsProtocolEventReceived-=OnFinished;
            failed.DevToolsProtocolEventReceived-=OnFailed;
            pending.Clear();
        }
    }
}
