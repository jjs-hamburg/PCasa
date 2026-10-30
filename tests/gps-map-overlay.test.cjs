'use strict';
const fs=require('node:fs');
const vm=require('node:vm');
const assert=require('node:assert/strict');
const html=fs.readFileSync('src/PCasa.App/Resources/gps-map.html','utf8');
const script=html.match(/<script>\s*(\(\(\) => \{[\s\S]*?\}\)\(\);)\s*<\/script>/)?.[1];
assert.ok(script,'Embedded map controller is present');
let canvasDraw=[], messages=[],activeMap;
const context={
  setTransform(){},clearRect(){canvasDraw=[];},beginPath(){},
  arc(x,y,r){canvasDraw.push({x,y,r});},fill(){},stroke(){},fillText(t){messages.push({label:t});}
};
const host={clientWidth:1024,clientHeight:650,appendChild(){},querySelector(){return null;}};
const document={
  getElementById(){return {remove(){},textContent:''};},
  createElement(kind){return kind==='canvas'
    ? {style:{},width:0,height:0,getContext(){return context;}}
    : {style:{},classList:{toggle(){}},addEventListener(){}};}
};
class MapStub {
 constructor(){activeMap=this;this.handlers={};this.sources={};this.layers={};this.zoom=2;}
 on(name,a,b){(this.handlers[name]??=[]).push({layer:b?a:null,handler:b||a});}
 emit(name,event){for(const entry of this.handlers[name]??[])
   if(!entry.layer)entry.handler(event);}
 getContainer(){return host;}getCanvas(){return {style:{}};}
 addControl(){}addSource(id,source){this.sources[id]={setData(data){this.data=data;}};}
 getSource(id){return this.sources[id];}addLayer(layer){this.layers[layer.id]=layer;}
 getLayer(id){return this.layers[id];}
 project(location){return {x:512+location[0]*4,y:325-location[1]*4};}
 fitBounds(b){this.bounds=b.points;}
 easeTo(args){this.lastEase=args;}
 getZoom(){return this.zoom;}resize(){}
 getCenter(){return {lat:0,lng:0};}getBounds(){return {getSouth:()=>-90,getWest:()=>-180,getNorth:()=>90,getEast:()=>180};}
 setStyle(){}
}
class MarkerStub{constructor(){this.element={};}setLngLat(){return this;}addTo(){return this;}remove(){}}
class Bounds{constructor(){this.points=[];}extend(p){this.points.push(p);}}
const window={devicePixelRatio:1,requestAnimationFrame:f=>f(),CefSharp:{PostMessage:json=>messages.push(JSON.parse(json))}};
const maplibregl={Map:MapStub,Marker:MarkerStub,LngLatBounds:Bounds,NavigationControl:class{}};
window.maplibregl=maplibregl;
vm.runInNewContext(script,{window,document,Map,Number,console,maplibregl});
activeMap.emit('style.load');
assert.ok(messages.some(m=>m.type==='ready'));
const worldwide=Array.from({length:6000},(_,i)=>({
 id:i+1,name:'photo '+i,lat:i<3000?53.5:26.1,
 lon:i<3000?10:-80,selected:true
}));
window.pcasaBeginPoints(1,3);
window.pcasaCommitPoints(1);
window.pcasaAppendPoints(1,2,worldwide.slice(4000));
window.pcasaAppendPoints(1,0,worldwide.slice(0,2000));
assert.equal(canvasDraw.length,0,'No partial world view before all chunks');
window.pcasaAppendPoints(1,1,worldwide.slice(2000,4000));
assert.equal(activeMap.bounds.length,6000,'Fit uses all continents');
const clusters=canvasDraw.filter(c=>c.r===16);
assert.equal(clusters.length,2,'Both distinct continents must visibly render');
assert.ok(clusters[0].x!==clusters[1].x);
assert.deepEqual(messages.filter(m=>m.label==='3000').length,2);
assert.ok(messages.some(m=>m.type==='gps-sync'&&m.received===6000&&m.selected===6000));
const small=[
 {id:1,lat:52,lon:10,selected:true},
 {id:2,lat:35,lon:-118,selected:true},
 {id:3,lat:-24,lon:133,selected:true}
];
window.pcasaBeginPoints(2,2);
window.pcasaAppendPoints(2,1,small.slice(1));
window.pcasaCommitPoints(2);
window.pcasaAppendPoints(2,0,small.slice(0,1));
assert.equal(canvasDraw.filter(c=>c.r===10).length,3,
 'Every separated selected location renders for small selections');
activeMap.emit('click',{point:{x:512+10*4,y:325-52*4},lngLat:{lat:52,lng:10}});
assert.ok(messages.some(m=>m.type==='select'&&m.id===1),'Map click selects photo');
activeMap.emit('style.load');
assert.equal(canvasDraw.filter(c=>c.r===10).length,3,'Style reload retains selected points');
window.pcasaBeginPoints(3,1);window.pcasaBeginPoints(4,1);
window.pcasaAppendPoints(3,0,worldwide);window.pcasaCommitPoints(3);
window.pcasaCommitPoints(4);window.pcasaAppendPoints(4,0,small);
assert.equal(canvasDraw.filter(c=>c.r===10).length,3,'Stale transfer is ignored');
console.log('GPS map overlay PASS: 6000 selected, worldwide clusters, small selection, stale chunks, style reload, click');
