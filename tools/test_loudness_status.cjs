const {JSDOM}=require('jsdom'),fs=require('fs'),assert=require('assert');
const source=fs.readFileSync(require('path').join(__dirname,'../src/Lowfin.Companion/Configuration.html'),'utf8');
const flush=async()=>{for(let i=0;i<5;i++)await new Promise(resolve=>setImmediate(resolve));};
const ready=(overrides={})=>({running:false,itemsChecked:4,profilesMeasured:2,profilesFailed:0,queued:0,stored:{ready:true,failed:false,profiles:12,items:4},...overrides});
function fixture(reply){
    let now=0,serial=0,hidden=false,calls=0,inFlight=0,maxInFlight=0;const timers=new Map();
    const dom=new JSDOM(source,{runScripts:'dangerously',pretendToBeVisual:true,url:'http://localhost/web/index.html',beforeParse(w){
        w.Date.now=()=>now;w.setTimeout=(fn,delay=0)=>{const id=++serial;timers.set(id,{fn,at:now+delay});return id;};w.clearTimeout=id=>timers.delete(id);
        w.Dashboard={showLoadingMsg(){},hideLoadingMsg(){}};
        w.ApiClient={getPluginConfiguration:async()=>({AudioLoudnessEnabled:false}),getUsers:async()=>[],updatePluginConfiguration:async()=>{},getUrl:path=>path,ajax:async options=>{
            if(!options.url.includes('audio-loudness/status'))return {};
            calls++;inFlight++;maxInFlight=Math.max(maxInFlight,inFlight);
            try{return await reply(calls);}finally{inFlight--;}
        }};
    }});
    const w=dom.window,d=w.document,page=d.getElementById('lowfinCompanionPage');Object.defineProperty(d,'hidden',{get:()=>hidden});
    return {dom,page,text:()=>d.getElementById('audioLoudnessStatus').textContent,calls:()=>calls,maxInFlight:()=>maxInFlight,
        async event(name){page.dispatchEvent(new w.Event(name));await flush();},
        async refresh(){d.getElementById('loadAudioLoudness').click();await flush();},
        async visibility(value){hidden=value;d.dispatchEvent(new w.Event('visibilitychange'));await flush();},
        async advance(ms){const end=now+ms;let steps=0;while(true){await flush();const next=[...timers].filter(([,v])=>v.at<=end).sort((a,b)=>a[1].at-b[1].at)[0];if(!next)break;assert(++steps<100,'Unexpected polling loop');now=next[1].at;timers.delete(next[0]);next[1].fn();}now=end;await flush();},
        close(){page.dispatchEvent(new w.Event('pagehide'));dom.window.close();}
    };
}
(async()=>{
    let f=fixture(n=>ready(n===1?{stored:{ready:false,failed:false,profiles:0,items:0}}:{}));
    try{await f.event('pageshow');assert.match(f.text(),/Counting stored profiles/);await f.advance(2000);assert.match(f.text(),/12 stored profiles across 4 media items/);assert.equal(f.calls(),2);await f.advance(20000);assert.equal(f.calls(),2,'Idle totals must stop polling');}finally{f.close();}

    f=fixture(n=>ready({stored:{Ready:n>1,Failed:false,Profiles:n>1?12:0,Items:n>1?4:0}}));
    try{await f.event('pageshow');assert.match(f.text(),/Counting stored profiles/);await f.advance(2000);assert.match(f.text(),/12 stored profiles across 4 media items/);await f.advance(20000);assert.equal(f.calls(),2,'Legacy PascalCase ready status must stop polling');}finally{f.close();}

    f=fixture(()=>ready({stored:{Ready:true,Failed:false,Profiles:12}}));
    try{await f.event('pageshow');assert.match(f.text(),/Stored totals unavailable/);await f.advance(10000);assert.equal(f.calls(),1,'Invalid status schema must not pretend to count forever');}finally{f.close();}

    f=fixture(n=>ready(n<3?{running:true,profilesMeasured:n}:{}));
    try{await f.event('pageshow');await f.event('viewshow');assert.equal(f.calls(),1,'Duplicate lifecycle events must not duplicate requests');await f.advance(4000);assert.match(f.text(),/Last run: idle/);assert.equal(f.calls(),3);await f.advance(10000);assert.equal(f.calls(),3);}finally{f.close();}

    f=fixture(()=>ready({running:true}));
    try{await f.event('pageshow');await f.visibility(true);await f.advance(10000);assert.equal(f.calls(),1);await f.visibility(false);assert.equal(f.calls(),2);await f.event('viewhide');await f.advance(10000);assert.equal(f.calls(),2,'A closed page must stop polling');await f.event('viewshow');assert.equal(f.calls(),3);}finally{f.close();}

    let resolveFirst;const pending=new Promise(resolve=>{resolveFirst=resolve;});
    f=fixture(n=>n===1?pending:ready());
    try{await f.event('pageshow');await f.refresh();await f.refresh();assert.equal(f.calls(),1);resolveFirst(ready({stored:{ready:false,failed:false,profiles:0,items:0}}));await flush();await f.advance(0);assert.equal(f.calls(),2);assert.equal(f.maxInFlight(),1);assert.match(f.text(),/12 stored profiles/);}finally{f.close();}

    let resolveOld;const stale=new Promise(resolve=>{resolveOld=resolve;});
    f=fixture(n=>n===1?stale:ready());
    try{await f.event('pageshow');await f.event('pagehide');await f.event('pageshow');resolveOld(ready({stored:{ready:false,failed:false,profiles:0,items:0}}));await flush();assert.doesNotMatch(f.text(),/Counting stored profiles/,'Old response must not overwrite the reopened view');await f.advance(0);assert.equal(f.calls(),2);assert.match(f.text(),/12 stored profiles/);}finally{f.close();}

    f=fixture(n=>{if(n===1)throw new Error('Fixture network failure');return ready();});
    try{await f.event('pageshow');assert.equal(f.text(),'Measurement status unavailable.');await f.advance(10000);assert.equal(f.calls(),1);await f.refresh();assert.match(f.text(),/12 stored profiles/);}finally{f.close();}

    f=fixture(()=>ready());
    try{await f.event('pageshow');const d=f.dom.window.document;d.getElementById('AudioLoudnessEnabled').checked=true;d.getElementById('lowfinCompanionForm').dispatchEvent(new f.dom.window.Event('submit',{cancelable:true}));await flush();await f.advance(10000);const count=f.calls();assert(count>2,'Saving enabled measurement must follow its task startup');await f.advance(60000);assert.equal(f.calls(),count,'Task-start watch must remain bounded when no task runs');}finally{f.close();}

    f=fixture(()=>ready({stored:{ready:false,failed:true,profiles:0,items:0}}));
    try{await f.event('pageshow');assert.match(f.text(),/Stored totals unavailable/);await f.advance(10000);assert.equal(f.calls(),1);}finally{f.close();}
    console.log('PASS: counting-to-ready, run completion, lifecycle/visibility, single-flight refresh, stale responses, failure/manual retry and idle polling stop.');
})().catch(error=>{console.error(error);process.exit(1);});
