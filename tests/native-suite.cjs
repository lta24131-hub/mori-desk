const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const {spawn} = require('node:child_process');
const root=path.resolve(__dirname,'..');
const runtime=path.resolve(root,process.argv[2]||'verified');
const library=path.join(root,'test-output','native-'+Date.now());
const image=fs.readFileSync(path.join(root,'dependencies','qingjie-icon.png')).toString('base64');
const input=[{action:'folders'},{action:'unknown'},
  {action:'save',kind:'video',name:'Not a video',data:image,duplicateAction:'reuse'},
  // Legacy image callers intentionally omit kind. Updated image callers send it.
  {action:'save',name:'Synthetic native protocol test',data:image,duplicateAction:'reuse',source:'https://example.com/test'},
  {action:'save',kind:'image',data:image,duplicateAction:'reuse'},
  {action:'save',kind:'video',data:image,duplicateAction:'reuse'},
  {action:'save',kind:'image',data:image,duplicateAction:'keep',source:'https://user:PRIVATE-PASSWORD@example.com/image'},
  {action:'save',kind:'image',data:image,duplicateAction:'reuse',source:'https://example.com/a%2Fb?q=one%20two#image%203'}];
const child=spawn(path.join(runtime,'Mori.exe'),['--native-test',library],{windowsHide:true,stdio:['pipe','pipe','pipe']});
const replies=[];let buffer=Buffer.alloc(0),error='';
child.stdout.on('data',b=>{buffer=Buffer.concat([buffer,b]);while(buffer.length>=4){const length=buffer.readUInt32LE();assert(length<1048576);if(buffer.length<length+4)return;replies.push(JSON.parse(buffer.subarray(4,length+4).toString()));buffer=buffer.subarray(length+4);}});
child.stderr.on('data',b=>error+=b);
const timer=setTimeout(()=>{child.kill();throw Error('Native protocol timeout');},20000);
child.on('exit',code=>{clearTimeout(timer);assert.equal(code,0,error);assert.equal(replies.length,input.length);assert.equal(replies[0].ok,true);assert.equal(replies[1].ok,false);assert.equal(replies[2].ok,false);assert.match(replies[2].error,/而不是可保存的视频/);assert.equal(replies[3].ok,true,JSON.stringify(replies[3]));assert.equal(replies[3].name,'Synthetic native protocol test');assert.equal(replies[4].ok,true);assert.equal(replies[4].duplicate,true);assert.equal(replies[5].ok,false);assert.equal(replies[5].choiceRequired,undefined);assert.match(replies[5].error,/而不是可保存的视频/);assert.equal(replies[6].ok,false);assert.match(replies[6].error,/不含账号密码/);assert.doesNotMatch(replies[6].error,/PRIVATE-/);assert.equal(replies[7].ok,true);assert.equal(buffer.length,0);console.log('PASS: isolated native protocol folders / error / media intent rejection before import and dedup / safe source metadata / legacy and current image save / clean shutdown. Library: '+library);});
for(const message of input){const body=Buffer.from(JSON.stringify(message));const size=Buffer.alloc(4);size.writeUInt32LE(body.length);child.stdin.write(Buffer.concat([size,body]));}child.stdin.end();
