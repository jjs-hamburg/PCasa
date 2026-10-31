'use strict';
const fs=require('node:fs');
const vm=require('node:vm');
const assert=require('node:assert/strict');
const html=fs.readFileSync('src/PCasa.App/Resources/gps-map.html','utf8');
const script=html.match(/<script>\s*(\(\(\) => \{[\s\S]*?\}\)\(\);)\s*<\/script>/)?.[1];
assert.ok(script,'Embedded map controller is present');

let messages=[],activeMap,markers=[];
const host={clientWidth:1024,clientHeight:650,appendChild(){},querySelector(){return null;}};
const document={
  getElementById(){return {remove(){},textContent:''};},
  createElement(){return {style:{},classList:{toggle(){}},addEventListener(){}};}
};
class MapStub {
  constructor(){activeMap=this;this.handlers={};this.sources={};this.layers={};this.zoom=2;}
  on(name,a,b){(this.handlers[name]??=[]).push({layer:b?a:null,handler:b||a});}
  emit(name,event){for(const x of this.handlers[name]??[])if(!x.layer)x.handler(event);}
  emitLayer(name,layer,event){for(const x of this.handlers[name]??[])
    if(x.layer===layer)x.handler(event);}
  getContainer(){return host;}getCanvas(){return {style:{}};}
  addControl(){}
  addSource(id,options){this.sources[id]={options,data:options.data,
    setData(data){this.data=data;},
    getClusterExpansionZoom(){return Promise.resolve(11);}};}
  getSource(id){return this.sources[id];}
  addLayer(layer){this.layers[layer.id]=layer;}
  getLayer(id){return this.layers[id];}
  queryRenderedFeatures(){return [];}
  project(location){return {x:512+location[0]*4,y:325-location[1]*4};}
  fitBounds(b){this.bounds=b.points;}
  easeTo(args){this.lastEase=args;}
  getZoom(){return this.zoom;}
  resize(){}
  getCenter(){return {lat:0,lng:0};}
  getBounds(){return {getSouth:()=>-90,getWest:()=>-180,getNorth:()=>90,getEast:()=>180};}
  setStyle(){this.sources={};this.layers={};}
}
class MarkerStub {
  constructor({element}={}){this.element=element;this.removed=false;markers.push(this);}
  setLngLat(coordinates){this.coordinates=coordinates;return this;}
  addTo(){this.attached=true;return this;}
  remove(){this.removed=true;}
}
class Bounds{constructor(){this.points=[];}extend(p){this.points.push(p);}}
const liveMarkers=()=>markers.filter(m=>m.attached&&!m.removed);
const markerCount=()=>liveMarkers().reduce((n,m)=>n+(Number(m.element.textContent)||1),0);
const window={devicePixelRatio:1,requestAnimationFrame:f=>f(),
  CefSharp:{PostMessage:json=>messages.push(JSON.parse(json))}};
const maplibregl={Map:MapStub,Marker:MarkerStub,LngLatBounds:Bounds,NavigationControl:class{}};
window.maplibregl=maplibregl;
vm.runInNewContext(script,{window,document,Map,Number,console,maplibregl});
activeMap.emit('style.load');
assert.ok(messages.some(m=>m.type==='ready'));
assert.ok(activeMap.getSource('pcasa-photos'),'Context source still exists');
assert.equal(activeMap.getSource('pcasa-selected'),undefined,
  'Selected photos must never use the formerly blank worker GeoJSON path');

const push=(version,data,chunkSize=180)=>{
  const chunks=Math.ceil(data.length/chunkSize);
  window.pcasaBeginPoints(version,chunks);
  window.pcasaCommitPoints(version);
  for(let i=0;i<chunks;i++)
    window.pcasaAppendPoints(version,i,data.slice(i*chunkSize,(i+1)*chunkSize));
};
const worldwide=Array.from({length:6000},(_,i)=>({
  id:i+1,name:'photo '+i,lat:i<3000?53.5:26.1,
  lon:i<3000?10:-80,selected:true}));
window.pcasaBeginPoints(1,3);
window.pcasaCommitPoints(1);
window.pcasaAppendPoints(1,2,worldwide.slice(4000));
window.pcasaAppendPoints(1,0,worldwide.slice(0,2000));
assert.equal(liveMarkers().length,0,'No partially transferred selection may render');
window.pcasaAppendPoints(1,1,worldwide.slice(2000,4000));
assert.equal(markerCount(),6000);
assert.equal(liveMarkers().length,2,'Exact duplicates aggregate geographically');
assert.equal(activeMap.bounds.length,6000,'Fit bounds based on original GPS');
assert.ok(messages.some(m=>m.type==='gps-sync'&&m.received===6000&&m.selected===6000));
// Click on a counted exact position preserves filmstrip group semantics.
const eu=liveMarkers().find(m=>m.coordinates[0]===10);
activeMap.emit('click',{point:activeMap.project(eu.coordinates)});
const firstGroup=messages.find(m=>m.type==='point-group');
assert.equal(firstGroup.id,1);
assert.equal(firstGroup.south,53.5);

// The ONE marker path also renders a small selection.
const small=[
  {id:1,lat:52,lon:10,selected:true},
  {id:2,lat:35,lon:-118,selected:true},
  {id:3,lat:-24,lon:133,selected:true}];
push(2,small);
assert.equal(liveMarkers().length,3);
assert.equal(markerCount(),3);
activeMap.emit('click',{point:activeMap.project([10,52])});
assert.ok(messages.some(m=>m.type==='select'&&m.id===1));
activeMap.setStyle();activeMap.emit('style.load');
assert.equal(liveMarkers().length,3,'Provider style replacement keeps map markers');
window.pcasaBeginPoints(3,1);window.pcasaBeginPoints(4,1);
window.pcasaAppendPoints(3,0,worldwide);window.pcasaCommitPoints(3);
window.pcasaCommitPoints(4);window.pcasaAppendPoints(4,0,small);
assert.equal(liveMarkers().length,3,'A stale transfer cannot overwrite newer selection');

// Geographic proximity must be measured in km, not equal GPS strings and
// NOT by moving near-by markers onto a fabricated lat/lon grid line.
const tenerife=Array.from({length:17},(_,i)=>({
  id:8000+i,selected:true,
  lat:28.415+i*0.00023,lon:-16.568+i*0.00017}));
activeMap.zoom=11; // ~0.55km clustering radius
push(5,tenerife);
assert.ok(liveMarkers().length<17,
  'Real coordinates differing by tens of meters must form geographical clusters');
assert.equal(markerCount(),17);
const grouped=liveMarkers().find(m=>Number(m.element.textContent)>1);
assert.ok(grouped,'Nearby photos are grouped even without identical GPS');
assert.ok(grouped.coordinates[0] < -16.565 && grouped.coordinates[0] > -16.569,
  'Cluster center is derived from actual GPS longitudes');
activeMap.lastEase=undefined;
activeMap.emit('click',{point:activeMap.project(grouped.coordinates)});
assert.ok(activeMap.lastEase&&activeMap.lastEase.zoom>11,
  'Clicking several distinct positions zooms into that geographic group');
assert.equal(messages.filter(m=>m.type==='point-group').length,1,
  'Near-but-different positions must NOT be treated as identical originals');

// Higher map zoom reduces the actual geographic radius and resolves the
// same photos into the ORIGINAL underlying GPS positions, no pixel offsets.
activeMap.zoom=20;
activeMap.emit('moveend');
assert.equal(liveMarkers().length,17,'High zoom resolves nearby real GPS positions');
assert.equal(markerCount(),17);
for(const photo of tenerife)
  assert.ok(liveMarkers().some(m=>
    Math.abs(m.coordinates[0]-photo.lon)<1e-9 &&
    Math.abs(m.coordinates[1]-photo.lat)<1e-9));

// More than 7,000 selected real worldwide GPS locations must never create
// a BLANK world map. There is no special >64-photo renderer any more.
activeMap.zoom=2;
const world7200=Array.from({length:7200},(_,i)=>{
  const region=Math.floor(i/1440),j=i%1440;
  const base=[{lat:50,lon:10},{lat:38,lon:-120},{lat:-28,lon:135},
    {lat:-15,lon:-65},{lat:15,lon:100}][region];
  return {id:20000+i,lat:base.lat+(j%60)*0.031,
    lon:base.lon+Math.floor(j/60)*0.034,selected:true};
});
push(6,world7200);
assert.ok(liveMarkers().length>0&&liveMarkers().length<7200,
  'World map must have visible geographically clustered markers');
assert.equal(markerCount(),7200,'Every selected photo is counted exactly once');
assert.ok(messages.some(m=>m.type==='gps-sync'&&m.received===7200&&m.selected===7200));
const longs=liveMarkers().map(m=>m.coordinates[0]);
assert.ok(longs.some(l=>l<-110)&&longs.some(l=>l>130)&&
  longs.some(l=>l>0&&l<30),
  'North America, Australia and Europe all visible in world view');
// The provider swap may NOT requery or re-cluster by a provider-specific API.
const initial=liveMarkers().map(m=>[...m.coordinates,Number(m.element.textContent)||1]);
window.pcasaApplyProvider({format:'style',url:'https://example.test/vector.json'});
activeMap.emit('style.load');
assert.deepEqual(liveMarkers().map(m=>[...m.coordinates,Number(m.element.textContent)||1]),
  initial,'Vector style switch keeps exactly the same GPS clusters');
window.pcasaApplyProvider({format:'xyz',url:'https://example.test/{z}/{x}/{y}.png'});
activeMap.emit('style.load');
assert.deepEqual(liveMarkers().map(m=>[...m.coordinates,Number(m.element.textContent)||1]),
  initial,'Raster XYZ style switch keeps exactly the same GPS clusters');
assert.equal(markerCount(),7200);
assert.equal(activeMap.getSource('pcasa-selected'),undefined,
  'No independent map-provider-selected overlay may reappear');
console.log('GPS map PASS: 7200 selected photos on five continents, real-km zoom clustering, original coordinates, stable counts, group clicks, provider-independent DOM markers');
