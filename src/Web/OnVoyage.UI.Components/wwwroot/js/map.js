// MapLibre map for the PWA and the phone apps (§14.4). Vector tiles come from one PMTiles file; fonts are our own; tile.openstreetmap.org is never called.
import { Map as MapLibreMap, addProtocol } from "../lib/maplibre-gl/maplibre-gl.mjs";
import { Protocol } from "../lib/pmtiles/pmtiles.mjs";
import { layers, namedFlavor } from "../lib/basemaps/basemaps.mjs";

const CATEGORY_COLORS = {
    monument: "#b4533c", museum: "#6a4c93", nature: "#3f8f5b", viewpoint: "#d19a2a",
    religious: "#8a6f4d", neighborhood: "#0b6b7a", food: "#c2456b", default: "#0b6b7a",
};

let protocolAdded = false;
let map = null;
let dotnet = null;
let data = { type: "FeatureCollection", features: [] };
let me = null;
let settings = null;
let moveTimer = null;
let colorScheme = null;

export function init(container, reference, options) {
    dotnet = reference;
    settings = options;
    if (!protocolAdded) {
        addProtocol("pmtiles", new Protocol().tile);
        protocolAdded = true;
    }

    colorScheme = window.matchMedia("(prefers-color-scheme: dark)");
    map = new MapLibreMap({
        container,
        style: buildStyle(),
        center: [options.centerLng ?? 5.37, options.centerLat ?? 43.3],
        zoom: options.zoom ?? 12,
        attributionControl: false, // The attribution is a fixed element of the page, never collapsible (OSM licence).
    });
    map.on("style.load", addPoiLayers);
    map.on("moveend", () => {
        clearTimeout(moveTimer);
        moveTimer = setTimeout(reportViewport, 250);
    });
    colorScheme.addEventListener("change", () => map.setStyle(buildStyle()));
}

function buildStyle() {
    const flavor = colorScheme && colorScheme.matches ? "dark" : "light";
    return {
        version: 8,
        glyphs: settings.glyphsUrl,
        sources: { protomaps: { type: "vector", url: "pmtiles://" + settings.tilesUrl } },
        layers: layers("protomaps", namedFlavor(flavor), { lang: "fr" }),
    };
}

function categoryColor() {
    const stops = Object.entries(CATEGORY_COLORS).filter(([k]) => k !== "default").flat();
    return ["match", ["get", "category"], ...stops, CATEGORY_COLORS.default];
}

function addPoiLayers() {
    map.addSource("pois", { type: "geojson", data, cluster: true, clusterRadius: 50, clusterMaxZoom: 15 });
    map.addLayer({
        id: "poi-clusters", type: "circle", source: "pois", filter: ["has", "point_count"],
        paint: { "circle-color": "#0b6b7a", "circle-radius": ["step", ["get", "point_count"], 16, 10, 20, 50, 26], "circle-stroke-width": 2, "circle-stroke-color": "#ffffff" },
    });
    map.addLayer({
        id: "poi-cluster-count", type: "symbol", source: "pois", filter: ["has", "point_count"],
        layout: { "text-field": ["get", "point_count_abbreviated"], "text-font": ["Noto Sans Medium"], "text-size": 13 },
        paint: { "text-color": "#ffffff" },
    });
    map.addLayer({
        id: "poi-points", type: "circle", source: "pois", filter: ["!", ["has", "point_count"]],
        paint: {
            "circle-color": categoryColor(),
            "circle-radius": ["case", ["get", "audio"], 9, 7],
            "circle-stroke-width": ["case", ["get", "saved"], 4, 2],
            "circle-stroke-color": ["case", ["get", "saved"], "#e0a100", "#ffffff"],
        },
    });
    map.addSource("me", { type: "geojson", data: meData() });
    map.addLayer({ id: "me-accuracy", type: "circle", source: "me", paint: { "circle-color": "#2f7de1", "circle-opacity": 0.15, "circle-radius": ["coalesce", ["get", "r"], 20] } });
    map.addLayer({ id: "me-dot", type: "circle", source: "me", paint: { "circle-color": "#2f7de1", "circle-radius": 7, "circle-stroke-width": 3, "circle-stroke-color": "#ffffff" } });

    map.on("click", "poi-points", (e) => {
        const id = e.features && e.features[0] && e.features[0].properties.id;
        if (id) dotnet.invokeMethodAsync("OnPoiTapped", id);
    });
    map.on("click", "poi-clusters", async (e) => {
        const feature = e.features[0];
        const zoom = await map.getSource("pois").getClusterExpansionZoom(feature.properties.cluster_id);
        map.easeTo({ center: feature.geometry.coordinates, zoom });
    });
    for (const layer of ["poi-points", "poi-clusters"]) {
        map.on("mouseenter", layer, () => (map.getCanvas().style.cursor = "pointer"));
        map.on("mouseleave", layer, () => (map.getCanvas().style.cursor = ""));
    }
}

function meData() {
    return {
        type: "FeatureCollection",
        features: me ? [{ type: "Feature", geometry: { type: "Point", coordinates: [me.lng, me.lat] }, properties: { r: metersToPixels(me.accuracy, me.lat) } }] : [],
    };
}

function metersToPixels(meters, lat) {
    if (!map || !meters) return 20;
    const mpp = 156543.03392 * Math.cos((lat * Math.PI) / 180) / Math.pow(2, map.getZoom());
    return Math.max(10, Math.min(120, meters / mpp));
}

function reportViewport() {
    const b = map.getBounds();
    dotnet.invokeMethodAsync("OnViewportChanged", b.getWest(), b.getSouth(), b.getEast(), b.getNorth(), map.getZoom());
}

export function setPois(geojson) {
    data = geojson;
    const source = map && map.getSource("pois");
    if (source) source.setData(data);
}

export function setUserLocation(lat, lng, accuracy) {
    me = { lat, lng, accuracy };
    const source = map && map.getSource("me");
    if (source) source.setData(meData());
}

export function fitBounds(west, south, east, north) {
    if (map) map.fitBounds([[west, south], [east, north]], { padding: 48, maxZoom: 16, duration: 400 });
}

export function dispose() {
    clearTimeout(moveTimer);
    if (map) map.remove();
    map = null;
    dotnet = null;
}
