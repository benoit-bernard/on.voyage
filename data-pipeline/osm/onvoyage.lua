-- osm2pgsql flex style for ON.VOYAGE (cahier des charges §7.2). Version: 1.
-- Keeps NAMED objects of the families listed in the specification, as a point (nodes) or an area (closed ways, multipolygon relations).
-- The output table name comes from the ONVOYAGE_OSM_TABLE environment variable so every import lands in a dated table that is
-- swapped in only when it is complete.

local table_name = os.getenv('ONVOYAGE_OSM_TABLE') or 'osm_place'

local places = osm2pgsql.define_table({
    name = table_name,
    schema = 'factory_raw',
    ids = { type = 'any', id_column = 'osm_id', type_column = 'osm_type' },
    columns = {
        { column = 'name', type = 'text', not_null = true },
        { column = 'name_en', type = 'text' },
        { column = 'wikidata', type = 'text' },
        { column = 'version', type = 'int' },
        { column = 'tags', type = 'jsonb' },
        { column = 'geom', type = 'point', projection = 4326 },
        { column = 'area', type = 'multipolygon', projection = 4326 },
    },
})

local function one_of(value, set)
    return value ~= nil and set[value] == true
end

local function set(list)
    local result = {}
    for _, item in ipairs(list) do result[item] = true end
    return result
end

local tourism = set({ 'attraction', 'museum', 'gallery', 'viewpoint', 'artwork' })
local natural = set({ 'peak', 'cave_entrance', 'cliff', 'beach', 'bay', 'cape', 'spring', 'waterfall', 'rock', 'volcano' })
local man_made = set({ 'lighthouse', 'windmill', 'watermill', 'bridge' })
local building = set({ 'cathedral', 'chapel', 'church', 'castle' })
local shop = set({ 'cheese', 'wine' })
local place = set({ 'village', 'hamlet' })

local function wanted(tags)
    if not tags.name then return false end
    local has_wikidata = tags.wikidata ~= nil
    local has_heritage = tags.heritage ~= nil

    -- Heritage and military
    if tags.historic then
        if tags.historic == 'memorial' then return has_wikidata end
        return true
    end
    if has_heritage then return true end
    if one_of(tags.building, building) and has_wikidata then return true end

    -- Tourism and nature
    if one_of(tags.tourism, tourism) then return true end
    if one_of(tags.natural, natural) then return true end
    if tags.geological then return true end
    if tags.leisure == 'nature_reserve' then return true end
    if tags.boundary == 'protected_area' then return true end

    -- Worship and technical heritage need a reason to be notable
    if tags.amenity == 'place_of_worship' and (has_wikidata or has_heritage) then return true end
    if one_of(tags.man_made, man_made) and (has_wikidata or has_heritage) then return true end

    -- Villages
    if one_of(tags.place, place) then return true end

    -- Food and drink (low priority, notable ones only)
    if (tags.amenity == 'marketplace' or tags.craft == 'winery' or one_of(tags.shop, shop)) and has_wikidata then return true end

    return false
end

local function row_for(object)
    return {
        name = object.tags['name:fr'] or object.tags.name,
        name_en = object.tags['name:en'],
        wikidata = object.tags.wikidata,
        version = object.version,
        tags = object.tags,
    }
end

function osm2pgsql.process_node(object)
    if not wanted(object.tags) then return end
    local row = row_for(object)
    row.geom = object:as_point()
    places:insert(row)
end

function osm2pgsql.process_way(object)
    if not wanted(object.tags) or not object.is_closed then return end
    local row = row_for(object)
    row.area = object:as_polygon()
    places:insert(row)
end

function osm2pgsql.process_relation(object)
    local type = object:grab_tag('type')
    if type ~= 'multipolygon' and type ~= 'boundary' then return end
    if not wanted(object.tags) then return end
    local row = row_for(object)
    row.area = object:as_multipolygon()
    places:insert(row)
end
