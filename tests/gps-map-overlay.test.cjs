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
  emitLayer(name,layer,event){
    for(const x of this.handlers[name]??[])if(x.layer===layer)x.handler(event);
  }
  getContainer(){return host;}getCanvas(){return {style:{}};}
  addControl(){}
  addSource(id,options){this.sources[id]={data:options.data,setData(data){this.data=data;}};}
  getSource(id){return this.sources[id];}
  addLayer(layer){this.layers[layer.id]=layer;}
  getLayer(id){return this.layers[id];}
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
const window={devicePixelRatio:1,requestAnimationFrame:f=>f(),
  CefSharp:{PostMessage:json=>messages.push(JSON.parse(json))}};
const maplibregl={Map:MapStub,Marker:MarkerStub,LngLatBounds:Bounds,NavigationControl:class{}};
window.maplibregl=maplibregl;
vm.runInNewContext(script,{window,document,Map,Number,console,maplibregl});
activeMap.emit('style.load');
assert.ok(messages.some(m=>m.type==='ready'));
assert.ok(activeMap.getSource('pcasa-selected'),'MapLibre owns the selected image source');
assert.equal(activeMap.getLayer('pcasa-selected-points').type,'circle');
assert.equal(activeMap.getLayer('pcasa-selected-count').type,'symbol');
const selectedFeatures=()=>activeMap.getSource('pcasa-selected').data.features;
const selectedPhotoCount=()=>selectedFeatures().reduce((sum,f)=>sum+f.properties.count,0);

// Transfer is atomic: no partial continents before the last chunk. Same
// actual coordinate is counted, not redistributed across arbitrary pixels.
const worldwide=Array.from({length:6000},(_,i)=>({
 id:i+1,name:'photo '+i,lat:i<3000?53.5:26.1,
 lon:i<3000?10:-80,selected:true
}));
window.pcasaBeginPoints(1,3);
window.pcasaCommitPoints(1);
window.pcasaAppendPoints(1,2,worldwide.slice(4000));
window.pcasaAppendPoints(1,0,worldwide.slice(0,2000));
assert.equal(selectedFeatures().length,0,'No partial worldwide view');
window.pcasaAppendPoints(1,1,worldwide.slice(2000,4000));
assert.equal(activeMap.bounds.length,6000,'Fit bounds uses every selected coordinate');
assert.equal(selectedFeatures().length,2,'Identical GPS coordinates share exactly one pin');
assert.equal(selectedPhotoCount(),6000,'All 6,000 images counted');
assert.deepEqual(Array.from(selectedFeatures()).map(f=>f.properties.count),[3000,3000]);
assert.ok(messages.some(m=>m.type==='gps-sync'&&m.received===6000&&m.selected===6000));
const europe=selectedFeatures().find(f=>f.properties.lon===10);
activeMap.emitLayer('click','pcasa-selected-points',{features:[europe]});
const grouped=messages.find(m=>m.type==='point-group');
assert.ok(grouped,'Clicking a counted pin opens the corresponding photos');
assert.equal(grouped.id,1);
assert.equal(grouped.south,53.5);assert.equal(grouped.north,53.5);
assert.equal(grouped.west,10);assert.equal(grouped.east,10);
assert.equal(activeMap.lastEase,undefined,'Click opens photos instead of silently zooming');

// Small selections retain reliable individual MapLibre DOM markers.
const small=[
 {id:1,lat:52,lon:10,selected:true},
 {id:2,lat:35,lon:-118,selected:true},
 {id:3,lat:-24,lon:133,selected:true}
];
window.pcasaBeginPoints(2,2);
window.pcasaAppendPoints(2,1,small.slice(1));
window.pcasaCommitPoints(2);
window.pcasaAppendPoints(2,0,small.slice(0,1));
assert.equal(liveMarkers().length,3,'Every small-selection location has a DOM marker');
assert.equal(selectedFeatures().length,0,'Small selections use one marker implementation');
activeMap.emit('click',{point:{x:512+10*4,y:325-52*4},lngLat:{lat:52,lng:10}});
assert.ok(messages.some(m=>m.type==='select'&&m.id===1),'Map click opens exact photo');
activeMap.setStyle();activeMap.emit('style.load');
assert.equal(liveMarkers().length,3,'Style changes preserve small markers');
assert.ok(activeMap.getSource('pcasa-selected'),'MapLibre source is reinstalled after style reload');
window.pcasaBeginPoints(3,1);window.pcasaBeginPoints(4,1);
window.pcasaAppendPoints(3,0,worldwide);window.pcasaCommitPoints(3);
window.pcasaCommitPoints(4);window.pcasaAppendPoints(4,0,small);
assert.equal(liveMarkers().length,3,'Stale transfers cannot overwrite small selection');

window.pcasaBeginPoints(5,1);
window.pcasaAppendPoints(5,0,[{id:77,lat:53.5,lon:10,selected:true}]);
window.pcasaCommitPoints(5);
assert.equal(liveMarkers().length,1,'Single photo always has a DOM pin');
assert.deepEqual(Array.from(liveMarkers()[0].coordinates),[10,53.5]);
window.pcasaBeginPoints(6,1);
window.pcasaAppendPoints(6,0,[{id:77,lat:26.1,lon:-80,selected:true}]);
window.pcasaCommitPoints(6);
assert.deepEqual(Array.from(liveMarkers()[0].coordinates),[-80,26.1],
 'Corrected metadata repositions the SAME image marker');

// 6,100 DIFFERENT positions on three continents. Old tests covered only
// two locations, so they could not detect missing geographic places.
const distinct=Array.from({length:6100},(_,i)=>{
 const continent=Math.floor(i/2100);
 const local=i%2100;
 const base=[
   {lat:30,lon:-120},{lat:42,lon:8},{lat:-39,lon:120}
 ][Math.min(continent,2)];
 return {id:10000+i,lat:base.lat+(local%100)*0.0123,
   lon:base.lon+Math.floor(local/100)*0.0281,selected:true};
});
window.pcasaBeginPoints(7,1);
window.pcasaAppendPoints(7,0,distinct);window.pcasaCommitPoints(7);
assert.equal(liveMarkers().length,0,'Large selection releases the old DOM markers');
assert.equal(selectedFeatures().length,6100,
 'All 6100 DISTINCT GPS locations must enter the native geographic renderer');
assert.equal(selectedPhotoCount(),6100);
const longitudes=selectedFeatures().map(f=>f.geometry.coordinates[0]);
assert.ok(longitudes.some(lon=>lon< -110)&&
  longitudes.some(lon=>lon>0&&lon<25)&&longitudes.some(lon=>lon>110),
  'North America, Europe, and Australia must all be represented');
assert.deepEqual(Array.from(selectedFeatures()[0].geometry.coordinates),
 [distinct[0].lon,distinct[0].lat]);

// Previously the 36px grid wrongly combined separate real places into
// one artificial position, making straight rows on the map.
const close=[
 ...Array.from({length:63},(_,i)=>({id:9000+i,lat:20,lon:-50,selected:true})),
 {id:9100,lat:20,lon:-1.5,selected:true},
 {id:9101,lat:20,lon:4,selected:true}
];
window.pcasaBeginPoints(8,1);
window.pcasaAppendPoints(8,0,close);window.pcasaCommitPoints(8);
assert.equal(selectedFeatures().length,3,
 'Distinct locations must never merge based on pixel distance or grid cells');
assert.deepEqual(Array.from(selectedFeatures()).map(f=>f.properties.count),[63,1,1]);
assert.deepEqual(Array.from(selectedFeatures()).slice(1).map(f=>f.geometry.coordinates[0]),[-1.5,4]);
// MapLibre style reload reinstalls the selected source and restores ALL
// geographic features without requiring another WPF catalog query.
activeMap.setStyle();activeMap.emit('style.load');
assert.equal(selectedFeatures().length,3,'Style reload retains all exact geographic points');
assert.equal(selectedPhotoCount(),65);
console.log('GPS map PASS: 6100 distinct worldwide places, exact coordinates, stable small pins, counted duplicates, clicked groups and style reload');
