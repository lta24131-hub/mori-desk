const assert = require('node:assert/strict');
const {test} = require('node:test');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const context = vm.createContext({URL,AbortController,setTimeout,clearTimeout,Uint8Array,TextDecoder,btoa:value=>Buffer.from(value,'binary').toString('base64')});
vm.runInContext(fs.readFileSync(path.join(__dirname,'../browser-extension/media-source.js'),'utf8'),context);
const media = context.MoriMediaSource;
const code = 'SyntheticClip1';
const video = 'https://video.cdninstagram.com/fixture.mp4?signature=test';

test('oversized Blob video reports the 20 MB limit without falling back to another page, cover or bookmark',async()=>{
  const item={kind:'video',url:'blob:https://www.instagram.com/fixture',mediaPage:`https://www.instagram.com/reel/${code}/`};
  const doc={querySelectorAll:()=>[],querySelector:()=>null};
  for(const declared of [true,false]){
    let calls=0,canceled=0;
    const fetcher=async()=>{
      calls++;
      return {ok:true,
        headers:{get:key=>key==='content-type'?'video/mp4':declared?String(20*1024*1024+1):null},
        body:{getReader:()=>({read:async()=>({done:false,value:{length:20*1024*1024+1}}),cancel:async()=>{canceled++;}})}
      };
    };
    await assert.rejects(()=>media.resolve(item,doc,'https://www.instagram.com/',fetcher),error=>error.code==='MEDIA_TOO_LARGE'&&/超过 20 MB.*保存原文件/.test(error.message));
    assert.equal(calls,1,'oversized original is not replaced with another request');
    assert.equal(canceled,declared?0:1,'streaming overflow releases its reader');
  }
});
test('Reel identities are exact, query-free and scoped to Instagram', () => {
  for (const kind of ['reel','reels','p']) assert.equal(media.reel(`https://www.instagram.com/${kind}/${code}/?igsh=discarded`).code,code);
  for (const url of ['https://www.instagram.com/accounts/login/','https://instagram.com.evil.test/reel/abc123/','https://user:password@www.instagram.com/reel/abc123/','http://www.instagram.com/reel/abc123/','https://www.instagram.com/reels/']) assert.equal(media.reel(url),null);
});

test('profile-prefixed Reel permalinks retain their exact identity and reject extra routes', () => {
  for(const kind of ['reel','reels','p']) assert.equal(media.reel(`https://www.instagram.com/robot.studio/${kind}/${code}/?igsh=removed`).code,code);
  for(const address of [`https://www.instagram.com/one/two/reel/${code}/`,`https://www.instagram.com/name/reel/${code}/comments/`,`https://www.instagram.com/name/reels/`,`https://other.test/name/reel/${code}/`]) assert.equal(media.reel(address),null);
});
test('only full HTTPS MP4 CDN links qualify, never ranges, manifests or poster images', () => {
  assert.equal(media.direct(video),video);
  assert.equal(media.direct('https://video.fbcdn.net/full.mp4'),'https://video.fbcdn.net/full.mp4');
  for(const value of ['https://cdninstagram.com.evil.test/a.mp4','https://unrelated.test/a.mp4','https://video.cdninstagram.com/a_dashinit.mp4','https://video.cdninstagram.com/a.mp4?bytestart=10','https://video.cdninstagram.com/a.mp4?range=1-20','https://video.cdninstagram.com/a.jpg','https://video.cdninstagram.com/a.m3u8','blob:https://www.instagram.com/a','javascript:alert(1)']) assert.equal(media.direct(value),'');
});
test('metadata matching uses the chosen shortcode, never a neighboring clip or its cover', () => {
  const data = {items:[{code:'WrongClip1',video_url:'https://video.cdninstagram.com/wrong.mp4'},{shortcode:code,video_url:video,display_url:'https://video.cdninstagram.com/poster.jpg'}]};
  assert.equal(media.fromData(data,code),video);
  assert.equal(media.fromData(data,'AbsentClip'),'');
});
test('video versions select highest declared resolution without taking audio-only or partial files', () => {
  const data = {code,video_versions:[{width:3840,height:2160,url:'https://video.cdninstagram.com/x_dashinit.mp4'},{width:720,height:1280,url:video},{width:360,height:640,url:'https://video.cdninstagram.com/small.mp4'}]};
  assert.equal(media.fromData(data,code),video);
});
test('carousels and non-video nodes cannot be silently substituted with one video', () => {
  assert.equal(media.fromData({code,is_video:false,video_url:video},code),'');
  assert.equal(media.fromData({code,carousel_media:[{video_url:video}],video_url:video},code),'');
  assert.equal(media.fromData({code,edge_sidecar_to_children:{edges:[{node:{video_url:video}}]}},code),'');
});
test('unsupported, malformed and unrelated objects never yield a generic URL', () => {
  for (const value of [null,{},[],{code,url:video},{code,display_url:video},{code,video_url:'https://video.cdninstagram.com/cover.jpg'}]) assert.equal(media.fromData(value,code),'');
});

test('numeric media IDs retain every bit and never truncate a private-link suffix', () => {
  assert.equal(media.numericId('BAAAA'),'16777216');
  assert.equal(media.numericId('D__________'),'4611686018427387903');
  for (const value of [null,'','invalid!','A'.repeat(29)]) assert.equal(media.numericId(value),'');
});

test('pk-only and composite IDs identify the exact selected media, not adjacent IDs', () => {
  const shortcode='D__________',pk='4611686018427387903';
  for(const identity of [{pk},{id:pk},{id:pk+'_123456'}]) {
    const data={items:[{pk:'4611686018427387904',video_url:'https://video.cdninstagram.com/wrong.mp4'},{...identity,video_versions:[{url:video}]}]};
    assert.equal(media.fromData(data,shortcode),video);
  }
  assert.equal(media.fromData({pk:Number(pk),video_url:video},shortcode),'','rounded Number cannot identify a media item');
});

test('conflicting shortcodes and numeric IDs never yield a different video', () => {
  const shortcode='D__________',pk='4611686018427387903';
  for(const node of [{code:shortcode,pk:'4611686018427387904'},{shortcode:'OtherClip1',pk},{pk,id:'4611686018427387904'}]) {
    assert.equal(media.fromData({...node,video_url:video},shortcode),'');
  }
});

test('Polaris selected-media wrapper supports matching or inherited identity without traversing unrelated children', () => {
  const shortcode='D__________',pk='4611686018427387903';
  for(const child of [{video_versions:[{url:video}]},{pk,video_url:video}]) {
    assert.equal(media.fromData({data:{xig_polaris_media:{id:pk,if_not_gated_logged_out:child}}},shortcode),video);
  }
  for(const child of [null,{pk:'4611686018427387904',video_url:video},{is_video:false,video_url:video},{media_type:8,video_url:video},{recommendations:{video_url:video}}]) {
    assert.equal(media.fromData({id:pk,if_not_gated_logged_out:child},shortcode),'');
  }
});

test('numeric identity does not treat carousel parent or still image as video', () => {
  for(const data of [{pk:'4611686018427387903',media_type:1,video_url:video},{pk:'4611686018427387903',media_type:8,if_not_gated_logged_out:{video_url:video}}]) {
    assert.equal(media.fromData(data,'D__________'),'');
  }
});

const embedFixture = (value) => `requireLazy(["ServerJS"],function(S){S.handle({"contextJSON":${JSON.stringify(JSON.stringify(value))}});window.mustNotExecute=true;});`;
const embedData = () => ({context:{type:'GraphVideo',shortcode:code,copyright_blocked:false},gql_data:{shortcode_media:{shortcode:code,id:media.numericId(code),is_video:true,video_url:video}}});
test('public embed contextJSON returns only the chosen video without executing ServerJS',()=>{
  assert.equal(media.embedContext(embedFixture(embedData()),media.reel(`https://www.instagram.com/reel/${code}/`)),video);
  assert.equal(context.mustNotExecute,undefined);
});
test('public embed rejects other identities, images, carousels and restricted media',()=>{
  const target=media.reel(`https://www.instagram.com/reel/${code}/`);
  for(const change of [
    data=>data.context.shortcode='OtherClip1', data=>data.context.type='GraphImage',
    data=>data.context.copyright_blocked=true, data=>data.gql_data.shortcode_media.id='12',
    data=>data.gql_data.shortcode_media.is_video=false, data=>data.gql_data.shortcode_media.copyright_blocked=true,
    data=>data.gql_data.shortcode_media.is_embeds_disabled=true, data=>data.gql_data.shortcode_media.carousel_media=[],
    data=>data.gql_data.shortcode_media.video_url='https://video.cdninstagram.com/x_dashinit.mp4',
    data=>delete data.gql_data
  ]) {const data=embedData();change(data);assert.equal(media.embedContext(embedFixture(data),target),'');}
});
test('embed extraction bounds data and never evaluates expressions in place of JSON',()=>{
  const target=media.reel(`https://www.instagram.com/reel/${code}/`);
  for(const text of ['{"contextJSON":(()=>{throw Error("executed")})()}', '{"contextJSON":"invalid json"}', 'x'.repeat(8*1024*1024+1)]) assert.equal(media.embedContext(text,target),'');
  assert.equal(media.embedContext(embedFixture(embedData()),media.reel('https://www.instagram.com/reel/OtherClip1/')),'');
});
test('embed addresses keep the selected identity and never accept a login or external redirect',()=>{
  const target=media.reel(`https://www.instagram.com/reels/${code}/`);
  assert.equal(media.embedUrl(target),`https://www.instagram.com/reel/${code}/embed/`);
  assert(media.isEmbedUrl(`https://www.instagram.com/p/${code}/embed/captioned/`,target));
  for(const value of ['https://www.instagram.com/accounts/login/',`https://www.instagram.com/reel/OtherClip1/embed/`,`https://evil.test/reel/${code}/embed/`,`https://user@www.instagram.com/reel/${code}/embed/`]) assert.equal(media.isEmbedUrl(value,target),false);
});
