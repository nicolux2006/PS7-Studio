'use strict';
(() => {
  const post = value => window.chrome.webview.postMessage(value);
  const theme = dark => dark
    ? {background:'#101820',foreground:'#d7e0e8',selectionBackground:'#2767ad',selectionInactiveBackground:'#405d80',selectionForeground:'#ffffff'}
    : {background:'#fafafa',foreground:'#202830',cursor:'#202830',selectionBackground:'#0067c0',selectionInactiveBackground:'#587899',selectionForeground:'#ffffff'};
  const term = new Terminal({cursorBlink:true,convertEol:false,scrollback:5000,fontFamily:'Cascadia Mono, Consolas, monospace',fontSize:13,allowProposedApi:false,screenReaderMode:true,theme:theme(true)});
  const fit = new FitAddon.FitAddon();
  term.loadAddon(fit);
  term.open(document.getElementById('terminal'));
  let lastCols=0,lastRows=0;
  const resize = () => {
    if (document.documentElement.clientWidth<30||document.documentElement.clientHeight<30) return;
    fit.fit();
    const cols=Math.max(2,Math.min(500,term.cols)),rows=Math.max(1,Math.min(300,term.rows));
    if(cols!==term.cols||rows!==term.rows)term.resize(cols,rows);
    if(term.cols!==lastCols||term.rows!==lastRows){lastCols=term.cols;lastRows=term.rows;post({type:'resize',cols:term.cols,rows:term.rows});}
  };
  new ResizeObserver(resize).observe(document.getElementById('terminal'));
  term.onData(data=>post({type:'input',data}));
  term.onBinary(data=>post({type:'binary',data}));
  term.attachCustomKeyEventHandler(event=>{
    if(event.type!=='keydown')return true;
    const copy=(event.ctrlKey&&event.code==='KeyC'&&(event.shiftKey||term.hasSelection()))||(event.ctrlKey&&event.code==='Insert');
    if(copy){event.preventDefault();if(term.hasSelection())post({type:'copy',data:term.getSelection()});return false;}
    if((event.ctrlKey&&event.shiftKey&&event.code==='KeyV')||(event.shiftKey&&event.code==='Insert')){event.preventDefault();post({type:'paste'});return false;}
    return true;
  });
  document.getElementById('terminal').addEventListener('contextmenu',event=>{
    event.preventDefault();post({type:'contextMenu',x:event.clientX,y:event.clientY,selected:term.hasSelection()});
  });
  window.chrome.webview.addEventListener('message',event=>{
    const msg=event.data;
    switch(msg.type){
      case 'write': term.write(msg.data,()=>post({type:'ack',id:msg.id}));break;
      case 'focus': term.focus();break;
      case 'clear': term.clear();break;
      case 'paste': term.paste(msg.data);break;
      case 'copy': post({type:'copy',data:term.getSelection()});break;
      case 'selectAll': term.selectAll();break;
      case 'settings':
        term.options.fontFamily=msg.font+', Consolas, monospace';term.options.fontSize=msg.fontSize;
        term.options.theme=theme(msg.dark);
        document.body.style.background=msg.dark?'#101820':'#fafafa';resize();break;
    }
  });
  resize();post({type:'ready'});
})();
