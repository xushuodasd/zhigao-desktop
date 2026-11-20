function (action, data) {
      if (location.origin !== 'https://zhuanlan.zhihu.com') return null;
      const visible = (node) => node && node.getClientRects().length &&
        getComputedStyle(node).visibility !== 'hidden';
      const matches = (scope, selector, pattern) => [...scope.querySelectorAll(selector)].filter((element) =>
        visible(element) && pattern.test((element.getAttribute('aria-label') || element.innerText || '').trim()));
      const enabled = (element) => !element.disabled && element.getAttribute('aria-disabled') !== 'true';
      const buttons = 'button,[role=button]';
      function unique(elements, click) {
        if (elements.length !== 1 || !enabled(elements[0])) return false;
        if (click) elements[0].click();
        return true;
      }
      const headline = [...document.querySelectorAll(data.titleSelector)].filter(visible);
      const editors = [...document.querySelectorAll(data.bodySelector)].filter(visible);
      const editor = editors[0];
      if (action === 'editor') return headline.length === 1 && editors.length === 1;
      if (action === 'contents') {
        if (headline.length !== 1 || editors.length !== 1) return null;
        const images = [...editor.querySelectorAll('img')];
        const readyImages = images.filter(img => img.complete && img.naturalWidth > 0 && /^https:\/\//i.test(img.currentSrc)).length;
        // Draft.js text runs exclude the contents of some atomic table blocks.
        const tableSelector = 'table,[role=table],[role=grid]';
        const tables = [];
        const listStarts = [];
        const seen = new Set();
        let length = 0;
        let text = '';
        function append(value) {
          text += value;
          length += value.replace(/[\s\u0085\u200b\ufeff]+/gu,'').length;
        }
        for (const node of editor.querySelectorAll('[data-text=true],'+tableSelector)) {
          if (node.parentElement.closest(tableSelector)) continue;
          if (node.matches(tableSelector)) {
            if (!visible(node)) continue;
            const start = length;
            const rows = [...node.querySelectorAll('tr,[role=row]')]
              .filter(row => row.closest(tableSelector) === node && visible(row))
              .map(row => [...row.querySelectorAll('td,th,[role=gridcell],[role=cell],[role=columnheader],[role=rowheader]')]
                .filter(cell => cell.closest('tr,[role=row]') === row && visible(cell))
                .map(cell => {
                  const value = cell.innerText;
                  append(value);
                  return value;
                }));
            const merged = [...node.querySelectorAll('[rowspan],[colspan],[aria-rowspan],[aria-colspan]')]
              .some(cell => ['rowspan','colspan','aria-rowspan','aria-colspan']
                .some(attr => cell.hasAttribute(attr) && Number(cell.getAttribute(attr)) !== 1));
            tables.push({ start, rows, merged });
            continue;
          }
          const item = node.closest('ol > li.public-DraftStyleDefault-orderedListItem');
          if (item && !seen.has(item)) { listStarts.push(length); seen.add(item); }
          append(node.textContent);
        }
        return { title: headline[0].value, text, listStarts, tables, hasContent: Boolean(editor.textContent.replace(/[\s\u200b\ufeff]+/gu,'')) || images.length > 0,
          imageCount: images.length,
          readyImages, imagesLoaded: images.length === readyImages };
      }
      if (action === 'import') return unique(matches(document, buttons, /^导入$/), true);
      function point(node) {
        if (!node) return null;
        node.scrollIntoView({block:'nearest', inline:'center'});
        const r = node.getBoundingClientRect();
        return {x:r.left+r.width/2, y:r.top+r.height/2};
      }
      if (action === 'import-point') {
        const hits = matches(document, buttons, /^导入$/);
        return hits.length === 1 ? point(hits[0]) : null;
      }
      if (action === 'import-doc' || action === 'doc-point') {
        const hits = matches(document, 'button,[role=menuitem]', /^导入文档(?:\s|MD|Doc|Word|$)/i);
        if(action === 'doc-point') return hits.length === 1 ? point(hits[0]) : null;
        return unique(hits, true);
      }
      if (action === 'title') {
        if (headline.length !== 1) return false;
        const input = headline[0];
        input.focus();
        input.select();
        return document.execCommand('insertText', false, data.title) && input.value === data.title;
      }
      const dialogs = [...document.querySelectorAll('[role=dialog],.Modal')].filter(visible);
      const dialog = dialogs.find((item) => !dialogs.some((other) => item !== other && item.contains(other)));
      if (action === 'dialog') return Boolean(dialog);
      if (action === 'confirm' || action === 'confirm-point') {
        if (!dialog || /必填|请选择|请添加|验证码|安全验证|重新登录/.test(dialog.innerText)) return false;
        const targets = matches(dialog, buttons, /^(确认发布|立即发布|确定发布|发布)$/);
        if (action === 'confirm-point') return unique(targets, false) ? point(targets[0]) : null;
        return unique(targets, true);
      }
      if (dialog || location.pathname !== '/write' && !/^\/p\/\d+\/edit\/?$/.test(location.pathname)) return false;
      const publish = matches(document, buttons, /^发布$/);
      if (action === 'unpublished-draft') return /^\/p\/\d+\/edit\/?$/.test(location.pathname) && publish.length===1;
      if (action === 'publish-point') return unique(publish, false) ? point(publish[0]) : null;
      return unique(publish, action === 'publish');
    }
