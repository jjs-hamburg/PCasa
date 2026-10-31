'use strict';
const fs=require('node:fs');
const vm=require('node:vm');
const assert=require('node:assert/strict');

const html=fs.readFileSync('src/PCasa.App/Resources/gps-map.html','utf8');
const script=html.match(/<script>\s*(\(\(\) => \{[\s\S]*?\}\)\(\);)\s*<\/script>/)?.[1];
assert.ok(script,'Embedded GPS map controller must exist');
let messages=[],activeMap,markers=[];
const host={clientWidth:1024,clientHeight:650,querySelector(){return null;}};
const document={
  getElementById(){return {remove(){},textContent:''};},
  createElement(){
    const classes=new Set();
    return {
      style:{},addEventListener(){},
      get className(){return [...classes].join(' ');},
      set className(value){classes.clear();for(const n of value.split(/\\s+/))if(n)classes.add(n);},
      classList:{
        add(...names){for(const n of names)classes.add(n);},
        remove(...names){for(const n of names)classes.delete(n);},
        contains(name){return classes.has(name);},
        toggle(name,force){
          const enable=force===undefined?!classes.has(name):Boolean(force);
          if(enable)classes.add(name);else classes.delete(name);
          return enable;
        }
      }
    };
  }
};
class MapStub {
  constructor(){activeMap=this;this.handlers={};this.sources={};this.layers={};this.zoom=2;}
  on(name,a,b){(this.handlers[name]??=[]).push({layer:b?a:null,callback:b||a});}
  emit(name,event={}){for(const h of this.handlers[name]??[])if(!h.layer)h.callback(event);}
  getContainer(){return host;}getCanvas(){return {style:{}};}
  addControl(){}
  addSource(id,opts){this.sources[id]={data:opts.data,setData(data){this.data=data;}};}
  getSource(id){return this.sources[id];}
  addLayer(obj){this.layers[obj.id]=obj;}
  getLayer(id){return this.layers[id];}
  queryRenderedFeatures(){return [];}
  project(x){return {x:512+x[0]*4,y:325-x[1]*4};}
  fitBounds(points){this.bounds=points;}
  easeTo(value){this.lastEase=value;}
  jumpTo(value){this.lastJump=value;}
  getZoom(){return this.zoom;}
  resize(){}
  getCenter(){return {lat:0,lng:0};}
  getBounds(){return {getSouth:()=>-90,getWest:()=>-180,getNorth:()=>90,getEast:()=>180};}
  setStyle(){this.sources={};this.layers={};}
}
class MarkerStub {
  constructor({element}={}){
    this.element=element;this.element.classList.add('maplibregl-marker');
    this.removed=false;markers.push(this);
  }
  setLngLat(coords){this.coordinates=coords;return this;}
  addTo(){this.attached=true;return this;}
  remove(){this.removed=true;}
}
const live=()=>markers.filter(x=>x.attached&&!x.removed);
const sum=()=>live().reduce((n,x)=>n+(Number(x.element.textContent)||1),0);
const window={devicePixelRatio:1,requestAnimationFrame:f=>f(),
  CefSharp:{PostMessage:x=>messages.push(JSON.parse(x))}};
const maplibregl={Map:MapStub,Marker:MarkerStub,NavigationControl:class{}};
window.maplibregl=maplibregl;
vm.runInNewContext(script,{window,document,Map,Number,console,maplibregl});
activeMap.emit('style.load');
assert.ok(messages.some(x=>x.type==='ready'));
assert.equal(activeMap.getSource('pcasa-selected'),undefined,
  'There must be no second selected GeoJSON renderer');

function send(version,items,total,selected,chunk=150){
  const chunks=Math.ceil(items.length/chunk);
  window.pcasaBeginClusters(version,chunks,total,selected);
  window.pcasaCommitClusters(version);
  for(let i=0;i<chunks;i++)
    window.pcasaAppendClusters(version,i,items.slice(i*chunk,(i+1)*chunk));
}
const big=[
  {id:1,lat:53.5,lon:10,count:3000,selected:true},
  {id:3001,lat:26.1,lon:-80,count:3000,selected:true}
];
window.pcasaBeginClusters(1,2,6000,6000);
window.pcasaAppendClusters(1,1,big.slice(1));
window.pcasaCommitClusters(1);
assert.equal(live().length,0,'Incomplete transfer cannot expose partial geography');
window.pcasaAppendClusters(1,0,big.slice(0,1));
assert.equal(live().length,2);
assert.ok(live().every(m=>m.element.classList.contains('maplibregl-marker')),
  'MapLibre absolute positioning class must survive first cluster draw');
assert.equal(sum(),6000,'Photo counts, not distinct positions, are transferred');
assert.ok(messages.some(x=>x.type==='gps-sync'&&x.received===6000&&x.selected===6000));
activeMap.emit('click',{point:activeMap.project([10,53.5])});
const clicked=messages.find(x=>x.type==='cluster-click');
assert.deepEqual({id:clicked?.id,version:clicked?.version},{id:1,version:1},
  'Click passes only identity + transfer version, never guesses photo coordinates');

const single=[{id:77,lat:28.415,lon:-16.568,count:1,selected:true}];
send(2,single,1,1);
assert.equal(live().length,1);
assert.equal(sum(),1);
activeMap.emit('click',{point:activeMap.project([-16.568,28.415])});
assert.ok(messages.some(x=>x.type==='cluster-click'&&x.id===77&&x.version===2),
  'Single point click also returns a PCasa-owned cluster ID');
window.pcasaHighlightCluster(77);
assert.ok(live()[0].element.className.includes('target'));
assert.ok(live()[0].element.classList.contains('maplibregl-marker'),
  'Highlighting one photo must not remove MapLibre geolocation layout class');
assert.deepEqual(Array.from(live()[0].coordinates),[-16.568,28.415],
  'A single point is drawn at its ORIGINAL GPS position');

const worldwide=[
  {id:101,lat:49,lon:10,count:1500,selected:true},
  {id:102,lat:40,lon:-120,count:1900,selected:true},
  {id:103,lat:-29,lon:134,count:800,selected:true},
  {id:104,lat:-14,lon:-65,count:1700,selected:true},
  {id:105,lat:17,lon:100,count:1300,selected:true}
];
send(3,worldwide,7200,7200,2);
assert.equal(live().length,5,'World-scale map draws ALL PCasa geographic clusters');
assert.ok(live().every(m=>m.element.classList.contains('maplibregl-marker')),
  'All 7200-photo world markers retain MapLibre CSS positioning, no vertical stripe');
assert.equal(sum(),7200,'All 7200 GPS photos appear in the global cluster counts');
assert.ok(live().some(x=>x.coordinates[0]< -110)&&
  live().some(x=>x.coordinates[0]>130)&&live().some(x=>x.coordinates[0]>0),
  'Locations in three continents survive the transfer');
window.pcasaFitGpsExtent(3,-29,-120,49,134);
assert.ok(activeMap.bounds,'Selection fits the actual photo extent');
window.pcasaFitGpsExtent(2,0,0,0,0);
assert.equal(activeMap.lastEase,undefined,'Old transfer cannot recenter the map');

const old=live().map(x=>JSON.stringify([...x.coordinates,x.element.textContent]));
window.pcasaApplyProvider({format:'style',url:'https://example.test/style.json'});
activeMap.emit('style.load');
assert.deepEqual(live().map(x=>JSON.stringify([...x.coordinates,x.element.textContent])),old,
  'Vector basemap switch cannot re-cluster photo positions');
window.pcasaApplyProvider({format:'xyz',url:'https://example.test/{z}/{x}/{y}.png'});
activeMap.emit('style.load');
assert.deepEqual(live().map(x=>JSON.stringify([...x.coordinates,x.element.textContent])),old,
  'Raster XYZ basemap switch cannot re-cluster photo positions');
activeMap.emit('moveend');
assert.ok(live().every(m=>m.element.classList.contains('maplibregl-marker')),
  'Zooming and style switches must never stack GPS pins in one vertical column');

window.pcasaBeginClusters(4,1,7200,7200);
window.pcasaAppendClusters(4,0,[{id:200,lat:0,lon:0,count:7,selected:true}]);
window.pcasaCommitClusters(4);
assert.equal(sum(),7200,'Incomplete membership must never silently replace valid data');
assert.ok(messages.some(x=>x.type==='error'&&x.message.includes('missing photo')));
send(5,single,1,1);
window.pcasaBeginClusters(6,1,6000,6000);
window.pcasaBeginClusters(7,1,1,1);
window.pcasaAppendClusters(6,0,big);window.pcasaCommitClusters(6);
window.pcasaAppendClusters(7,0,single);window.pcasaCommitClusters(7);
assert.equal(sum(),1,'Superseded transfers cannot republish old clusters');
console.log('GPS map PASS: 7200 photos, provider-independent cluster markers, click identity, single-photo popup protocol, counted atomic transfers and style switches');
