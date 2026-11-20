(function creatorEditor(operation, config, data) {
  if (location.origin + location.pathname + location.search + location.hash !== config.address) return null;

  const visible = element => element.getClientRects().length > 0 &&
    getComputedStyle(element).visibility !== 'hidden' && !element.closest('[hidden],[aria-hidden=true]');
  const candidates = (root, selector) => [...root.querySelectorAll(selector)].filter(visible);
  const titles = [...new Set(candidates(document, config.titleFields).filter(element =>
    !element.disabled && !element.readOnly &&
    (element.matches('input,textarea') || element.isContentEditable)).map(element=>
    element.closest('[contenteditable=true],[contenteditable=plaintext-only]') || element))];
  const editors = [];
  for (const element of candidates(document, config.bodyFields)) {
    if (element.tagName === 'IFRAME') {
      const frame = element.contentDocument;
      if (frame && frame.body && (frame.body.isContentEditable || frame.designMode === 'on')) editors.push(frame.body);
    } else if (element.isContentEditable) {
      editors.push(element);
    }
  }
  const isBody = e => !titles.some(title=>e.contains(title) || title.contains(e));
  const probableBodies=editors.filter(isBody);
  const candidatesWithoutTitle=[...new Set(probableBodies.length?probableBodies:
    candidates(document,"div[contenteditable=true]").filter(isBody))];
  const bodies=candidatesWithoutTitle.filter(element =>
    !candidatesWithoutTitle.some(other=>other!==element && element.contains(other)));
  if (titles.length !== 1 || bodies.length !== 1 || bodies[0].contains(titles[0])) return {state: 'waiting'};
  const [titleField] = titles;
  const [body] = bodies;
  const doc = body.ownerDocument;
  const win = doc.defaultView;
  const matchingEditors = doc===document ? [] : Object.values(window.UE_V2?.instants || {})
    .filter(editor=>editor.body===body && typeof editor.setContent==='function');
  const nativeEditor=matchingEditors.length===1 ? matchingEditors[0] : null;
  const compact = text => (text || '').replace(/[\s\u0085\u200b\ufeff]+/g, '');
  const title = () => (titleField.value ?? titleField.textContent).trim();
  const omitted = 'script,style,template,[contenteditable=false] [contenteditable=false],[aria-hidden=true],' +
    '.syl-placeholder.ProseMirror-widget[contenteditable=false],.Tiptap-invisible-character.ProseMirror-widget[contenteditable=false],' +
    'img.ProseMirror-separator,figure.wb-node-table > .wb-table-content__wrapper > .wb-table-content__wrapper__control';

  function snapshot() {
    const tables = [];
    const images = [];
    const lists = [];
    let text = '';
    function walk(node) {
      if (node.nodeType === 3) {
        text += compact(node.nodeValue);
        return;
      }
      if (node.nodeType !== 1 || node.matches(omitted)) return;
      if (location.host === 'mp.163.com' && node.matches('figcaption.rich-editor-image-remark') &&
          /^点击输入图片描述（最多\d+字）$/.test(node.textContent.trim())) return;
      if (node.tagName === 'IMG') {
        const url = node.currentSrc || node.getAttribute('src') || '';
        images.push({offset: text.length, durable: /^https:\/\//i.test(url), decoded: node.complete && node.naturalWidth > 0, url});
        return;
      }
      if (node.tagName === 'TABLE') {
        const table = {offset: text.length, cells: [], merged: false};
        tables.push(table);
        for (const row of node.rows) {
          const cells = [];
          for (const cell of row.cells) {
            const start = text.length;
            if (cell.colSpan !== 1 || cell.rowSpan !== 1) table.merged = true;
            for (const child of cell.childNodes) walk(child);
            cells.push(text.slice(start));
          }
          table.cells.push(cells);
        }
        return;
      }
      if (location.hostname === 'mp.sohu.com' && body.matches('.ql-editor') && node.tagName === 'OL') {
        const items = [...node.children];
        if (items.length && items.every(item => item.tagName === 'LI' && ['bullet','ordered'].includes(item.dataset.list))) {
          // Quill stores both kinds of lists in OL. A single container may hold successive lists.
          let group;
          for (const item of items) {
            const tag = item.dataset.list === 'bullet' ? 'ul' : 'ol';
            if (!group || group.tag !== tag) {
              group = {offset:text.length, tag, start:tag === 'ol' && item === items[0] ? node.start : 1, count:0};
              lists.push(group);
            }
            group.count++;
            walk(item);
          }
          return;
        }
      }
      if (node.tagName === 'UL' || node.tagName === 'OL') {
        lists.push({offset: text.length, tag: node.localName, start: node.tagName === 'OL' ? node.start : 1,
          count: [...node.children].filter(e => e.tagName === 'LI').length});
      }
      for (const child of node.childNodes) walk(child);
    }
    walk(body);
    return {state:'editor',title:title(),text,tables,images,lists};
  }

  function select(node, start, end) {
    const range = doc.createRange();
    if (node.nodeType === 3) {
      range.setStart(node, start);
      range.setEnd(node, end);
    } else {
      range.selectNodeContents(node);
    }
    body.focus();
    const selection = win.getSelection();
    selection.removeAllRanges();
    selection.addRange(range);
  }

  function paste(clipboard) {
    return body.dispatchEvent(new win.ClipboardEvent('paste', {
      bubbles:true, composed:true, cancelable:true, clipboardData:clipboard
    }));
  }

  function qiehaoDraftDeclaration() {
    const groups = candidates(document,'.omui-form__group').filter(group=>
      group.querySelector('label.omui-form__label')?.textContent.trim()==='自主声明');
    if (groups.length !== 1) return 'unavailable';
    if (candidates(groups[0],'.omui-contentbutton__title').some(e=>e.textContent.trim())) return 'selected';
    if (candidates(groups[0],'.omui-contentbutton__footer button').some(e=>e.textContent.trim()==='添加内容自主声明'))
      return 'missing';
    return 'unavailable';
  }

  if (operation === 'probe') return {state:'editor'};
  if (operation === 'inspect') return snapshot();
  if (operation === 'qiehao-draft-declaration' && location.origin === 'https://om.qq.com') return qiehaoDraftDeclaration();
  if (operation === 'weibo-draft-ready') {
    return !!body.querySelector(':scope > p.is-editor-empty');
  }
  if (operation === 'autosave-ready') {
    return [...document.querySelectorAll('span,label,p,div')].some(e=>visible(e) && !e.children.length &&
      /^(?:草稿)?已(?:自动)?保存(?:\s*[\d:年月日时分秒-]+)?$/.test(e.textContent.trim()));
  }
  if (operation === 'wangyi-autosave-ready') {
    const status = candidates(document,'.post-footer__container-left-save');
    return status.length === 1 && /^自动保存成功\s+\d{1,2}:\d{2}$/.test(status[0].textContent.trim());
  }
  if (operation === 'prepare-title') {
    const path = location.pathname.replace(/\/$/, '');
    const supported =
      (location.origin === 'https://baijiahao.baidu.com' && path === '/builder/rc/edit') ||
      (location.origin === 'https://om.qq.com' && path === '/main/creation/article');
    if (!supported || location.hash) return null;
    const params = new URLSearchParams(location.search);
    if (['draftId','articleId','article_id','group_id','contentId','id'].some(key => params.has(key))) return 'occupied';
    const before = snapshot();
    if (before.title || before.text || before.tables.length || before.images.length || before.lists.length)
      return 'occupied';
    if (typeof data?.title !== 'string' || !data.title.trim()) return 'title';
    if (titleField.maxLength >= 0 && data.title.length > titleField.maxLength) return 'title-length';
    titleField.focus({preventScroll:true});
    if (document.activeElement !== titleField) return 'title';
    if (titleField.matches('input,textarea')) {
      titleField.setSelectionRange(0, 0);
    } else {
      const selection = document.getSelection();
      const range = document.createRange();
      range.selectNodeContents(titleField);
      range.collapse(true);
      selection.removeAllRanges();
      selection.addRange(range);
    }
    return document.execCommand('insertText',false,data.title) ? 'started' : 'title';
  }
  if (operation === 'import') {
    const before = snapshot();
    if ((!data.titlePrepared && before.title) || before.text || before.tables.length || before.images.length || before.lists.length) return 'occupied';
    if (titleField.maxLength >= 0 && data.title.length > titleField.maxLength) return 'title-length';
    if (data.titlePrepared) {
      if (before.title !== data.title) return 'title';
    } else if (titleField instanceof HTMLInputElement || titleField instanceof HTMLTextAreaElement) {
      const proto = titleField instanceof HTMLInputElement ? HTMLInputElement.prototype : HTMLTextAreaElement.prototype;
      titleField.focus();
      Object.getOwnPropertyDescriptor(proto, 'value').set.call(titleField,data.title);
      titleField.dispatchEvent(new InputEvent('input',{bubbles:true,inputType:'insertText',data:data.title}));
      titleField.dispatchEvent(new Event('change',{bubbles:true}));
    } else {
      titleField.focus();
      if (document.activeElement===titleField) document.execCommand('insertText',false,data.title);
      if (!title()) {
        titleField.textContent=data.title;
        titleField.dispatchEvent(new InputEvent('input',{bubbles:true,inputType:'insertText',data:data.title}));
      }
    }
    if (title() !== data.title) return 'title';
    if (nativeEditor) {
      nativeEditor.setContent(data.html);
      return 'started';
    }
    select(body);
    const clip = new win.DataTransfer();
    clip.setData('text/html',data.html);
    const useDefault = paste(clip);
    const now = snapshot();
    if (useDefault && !now.text && !now.images.length && !now.tables.length) {
      select(body);
      if (!doc.execCommand('insertHTML',false,data.html)) {
        if (doc===document) return 'body';
        body.innerHTML=data.html;
        body.dispatchEvent(new win.InputEvent('input',{bubbles:true,inputType:'insertFromPaste'}));
      }
    }
    return 'started';
  }
  if (operation === 'image') {
    const nodes = [];
    const walker = doc.createTreeWalker(body, win.NodeFilter.SHOW_TEXT);
    while (walker.nextNode()) {
      const node = walker.currentNode;
      const start = node.nodeValue.indexOf(data.token);
      if (start >= 0 && node.nodeValue.indexOf(data.token,start+data.token.length) < 0) nodes.push([node,start]);
    }
    if (nodes.length !== 1) return false;
    const [node,start] = nodes[0];
    const bytes = win.Uint8Array.from(win.atob(data.base64),value=>value.charCodeAt(0));
    const clip = new win.DataTransfer();
    clip.items.add(new win.File([bytes],data.name,{type:data.mime}));
    select(node,start,start+data.token.length);
    paste(clip);
    return true;
  }
  if (operation === 'remove-token') {
    const walker = doc.createTreeWalker(body,win.NodeFilter.SHOW_TEXT);
    while (walker.nextNode()) {
      const node = walker.currentNode;
      const start = node.nodeValue.indexOf(data.token);
      if (start < 0) continue;
      select(node,start,start+data.token.length);
      return doc.execCommand('delete');
    }
    return false;
  }
  if (operation === 'save-draft' || operation === 'draft-readiness') {
    const fingerprint = item => JSON.stringify([item.title,item.text,
      item.tables.map(t=>[t.offset,t.merged,t.cells]), item.lists.map(l=>[l.offset,l.tag,l.start,l.count]),
      item.images.map(i=>[i.offset,i.durable,i.decoded])]);
    const sohu = location.origin === 'https://mp.sohu.com' &&
      location.pathname === '/mpfe/v4/contentManagement/news/addarticle';
    const controls = sohu ? 'button,[role=button],a,ul.button-list > li.publish-report-btn' : 'button,[role=button],a';
    const matching = candidates(document,controls).filter(e=>
      /^(?:保存草稿|保存为草稿|存草稿|暂存草稿)$/.test((e.innerText || e.textContent).trim()));
    const buttons = matching.filter(e=>!e.disabled && e.getAttribute('aria-disabled')!=='true' &&
      !(sohu && e.classList.contains('disable-button')));
    if (sohu && matching.length===1 && !buttons.length &&
      candidates(document,'ul.button-list > li.rest-number').some(e=>e.innerText.includes('未实名暂无法发布文章')))
      return 'sohu-verification-required';
    if (buttons.length !== 1) return false;
    if (operation === 'draft-readiness') return true;
    if (!data || fingerprint(snapshot()) !== fingerprint(JSON.parse(data.expected))) return false;
    buttons[0].click();
    return true;
  }
  return null;
})
