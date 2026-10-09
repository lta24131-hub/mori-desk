// Read only when explicitly requested. No background watching or remote requests.
globalThis.MoriBookmarks = {
  flatten(tree) {
    const result=[];let skipped=0,visited=0;
    function walk(nodes,path,depth) {
      if(depth>32)throw Error('书签文件夹层级过深');
      for(const node of nodes || []) {
        if(++visited>40000)throw Error('书签数量过多，请分批整理后导入');
        if(node.url) {
          let uri;try{uri=new URL(node.url);}catch{}
          if(!uri || !['https:','http:'].includes(uri.protocol) || uri.username || uri.password || node.url.length>8000 || /[\r\n]/.test(node.url)){skipped++;continue;}
          result.push({url:uri.href,name:String(node.title || uri.hostname).slice(0,250),path});
          if(result.length>20000)throw Error('单次最多导入 20000 个网址');
        } else walk(node.children,path.concat(node.title?[String(node.title).slice(0,120)]:[]),depth+1);
      }
    }
    walk(tree,[],0);return {items:result,skipped};
  }
};
