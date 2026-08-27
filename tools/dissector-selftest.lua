-- Self-test for docs/moza_dissector.lua.
--
--   lua5.4 tools/dissector-selftest.lua docs/moza_dissector.lua
--
-- Stubs enough of the Wireshark Lua API to load the dissector outside
-- Wireshark, then drives its decoders against the byte-exact example frames in
-- docs/protocol/. Covers the paths that no capture in usb-capture/ exercises
-- (group 0x2D cmd 0x77 LFE, group 0x24 cmd 0xB1 mBooster, group 0x35/0x36 CM1)
-- plus the session / telemetry / checksum paths as a cross-check against the
-- capture-driven verification.
--
-- Exits non-zero on any failed assertion.

local FIELD_NAMES = {}   -- table identity -> abbrev

-- ── ByteArray ──────────────────────────────────────────────────────────────
local BA = {}
BA.__index = BA
local function ba_new(bytes)
    return setmetatable({ b = bytes or {} }, BA)
end
function BA:len() return #self.b end
function BA:get_index(i) return self.b[i + 1] end
function BA:set_index(i, v) self.b[i + 1] = v end
function BA:set_size(n)
    for i = 1, n do if self.b[i] == nil then self.b[i] = 0 end end
end
function BA:tohex(lower)
    local t = {}
    for i = 1, #self.b do t[#t + 1] = string.format(lower and "%02x" or "%02X", self.b[i]) end
    return table.concat(t)
end
function BA:tvb(_) return TvbNew(self.b) end
ByteArray = { new = function() return ba_new({}) end }

-- ── Tvb / TvbRange ─────────────────────────────────────────────────────────
local TR = {}
TR.__index = TR
function TR:uint()
    local v = 0
    for i = self.off, self.off + self.len - 1 do v = v * 256 + self.src[i + 1] end
    return v
end
function TR:le_uint()
    local v = 0
    for i = self.off + self.len - 1, self.off, -1 do v = v * 256 + self.src[i + 1] end
    return v
end
function TR:float()
    -- IEEE-754 binary32, big-endian
    local b1, b2, b3, b4 = self.src[self.off + 1], self.src[self.off + 2],
                           self.src[self.off + 3], self.src[self.off + 4]
    local sign = (b1 >= 128) and -1 or 1
    local expo = ((b1 % 128) * 2) + (b2 // 128)
    local mant = ((b2 % 128) * 65536) + (b3 * 256) + b4
    if expo == 0 then
        if mant == 0 then return sign * 0.0 end
        return sign * math.ldexp(mant / 8388608, -126)
    end
    if expo == 255 then return mant == 0 and (sign * math.huge) or (0 / 0) end
    return sign * math.ldexp(1 + mant / 8388608, expo - 127)
end
function TR:bytes()
    local t = {}
    for i = self.off, self.off + self.len - 1 do t[#t + 1] = self.src[i + 1] end
    return ba_new(t)
end

function TvbNew(bytes)
    local src = bytes
    local tvb
    tvb = setmetatable({}, {
        __call = function(_, off, len)
            len = len or (#src - off)
            assert(off >= 0 and off + len <= #src,
                string.format("range out of bounds: off=%d len=%d src=%d", off, len, #src))
            return setmetatable({ src = src, off = off, len = len }, TR)
        end,
        __index = { len = function() return #src end },
    })
    return tvb
end

-- ── Tree ───────────────────────────────────────────────────────────────────
local Tree = {}
Tree.__index = Tree
local function tree_new(sink)
    return setmetatable({ sink = sink }, Tree)
end
local function record(self, field, range, value, le)
    local name = FIELD_NAMES[field]
    if name == nil then return end
    local v = value
    if v == nil and range ~= nil then
        v = le and range:le_uint() or range:uint()
    end
    self.sink[name] = v
    if range then self.sink[name .. ".hex"] = range:bytes():tohex(true) end
end
function Tree:add(field, range, value)
    if type(field) == "table" and field.__proto then
        -- tree:add(protoObj, range, label)
        return tree_new(self.sink)
    end
    record(self, field, range, value, false)
    return self
end
function Tree:add_le(field, range, value)
    record(self, field, range, value, true)
    return self
end
function Tree:append_text() return self end
function Tree:set_text() return self end
function Tree:add_proto_expert_info(e)
    self.sink["expert:" .. (e or "?")] = true
    return self
end

-- ── Proto / ProtoField / ProtoExpert ───────────────────────────────────────
local function mkfield(abbr)
    local f = {}
    FIELD_NAMES[f] = abbr
    return f
end
ProtoField = setmetatable({}, { __index = function(_, _)
    return function(abbr) return mkfield(abbr) end
end })
ProtoExpert = { new = function(abbr) return abbr end }
expert = {
    group = { CHECKSUM = 1, MALFORMED = 2, REASSEMBLE = 3, PROTOCOL = 4 },
    severity = { WARN = 1, NOTE = 2 },
}
function Proto(_, _)
    return { __proto = true, fields = {}, experts = {} }
end
function Field(_) return nil end
Field = { new = function() return function() return nil end end }
function register_postdissector() end
base = { HEX = 1, DEC = 2, NONE = 3 }

-- ── Load the dissector ─────────────────────────────────────────────────────
local path = arg[1] or "docs/moza_dissector.lua"
local src = io.open(path):read("a")
-- Expose the file's locals we want to test by appending a return table.
src = src .. [[

return {
    decode_2d               = decode_2d,
    decode_mbooster         = decode_mbooster,
    decode_cm1              = decode_cm1,
    decode_fsr1             = decode_fsr1,
    decode_chunk            = decode_chunk,
    decode_live_telemetry   = decode_live_telemetry,
    decode_wheel_cfg        = decode_wheel_cfg,
    crc32                   = crc32,
    wire_checksum           = wire_checksum,
    plain_checksum          = plain_checksum,
    cmd_lookup              = cmd_lookup,
}
]]
local M = assert(load(src, path))()

-- ── Test helpers ───────────────────────────────────────────────────────────
local pass, fail = 0, 0
local function check(label, got, want)
    if got == want or (type(got) == "number" and type(want) == "number"
                       and math.abs(got - want) < 1e-4) then
        pass = pass + 1
    else
        fail = fail + 1
        print(string.format("  FAIL %s: got %s want %s", label, tostring(got), tostring(want)))
    end
end

local function hexbytes(hex)
    local t = {}
    for pair in hex:gmatch("%x%x") do t[#t + 1] = tonumber(pair, 16) end
    return t
end

-- Run a decoder over a full frame `7E N grp dev payload... chk`.
local function run(fn, framehex)
    local bytes = hexbytes(framehex)
    local tvb = TvbNew(bytes)
    local n = bytes[2]
    local sink = {}
    fn(tvb, tree_new(sink), 4, n)
    return sink, tvb, n, bytes
end

print("== group 0x2D cmd 0x77 — LFE (docs/protocol/devices/wheelbase-0x13.md)")
do
    -- Engine effect, ParamK 1000, 40 Hz, 50% -> period=floor(1000/40)=25,
    -- freq=round(40/200*65536)=13107, intensity=round(50/100*65535)=32768
    local s = select(1, run(M.decode_2d, "7E0A2D13" .. "77" .. "00" .. "01" .. "01"
        .. string.format("%04X", 25)
        .. string.format("%04X", 13107)
        .. string.format("%04X", 32768) .. "00"))
    check("lfe effect id",  s["moza.lfe.effect"], 1)
    check("lfe play flag",  s["moza.lfe.play"], 1)
    check("lfe period",     s["moza.lfe.period"], 25)
    check("lfe freq raw",   s["moza.lfe.freq"], 13107)
    check("lfe amp raw",    s["moza.lfe.intensity"], 32768)
end

print("== group 0x2D F5 31 — sequence counter (docs/protocol/heartbeat.md)")
do
    local s = select(1, run(M.decode_2d, "7E062D13" .. "F531" .. "00000000" .. "00"))
    check("seq counter byte", s["moza.seq.counter"], 0x00)
    local s2 = select(1, run(M.decode_2d, "7E062D13" .. "F531" .. "0000002A" .. "00"))
    check("seq counter = 0x2A", s2["moza.seq.counter"], 0x2A)
end

print("== group 0x24 cmd 0xB1 — mBooster (Protocol/MozaMBoosterProtocol.cs)")
do
    -- Known-good from the source doc comment:
    -- ABS on, 22 Hz, amp=0x08e8: 7e 09 24 12 b1 01 01 00 5a 1c 28 08 e8 0b
    local s = select(1, run(M.decode_mbooster, "7E092412" .. "B1010100" .. "5A" .. "1C28" .. "08E8" .. "0B"))
    check("mb effect (ABS=1)", s["moza.mb.effect"], 1)
    check("mb enable",         s["moza.mb.enable"], 1)
    check("mb param1",         s["moza.mb.param1"], 0x5A)
    check("mb freq raw",       s["moza.mb.freq"], 0x1C28)
    check("mb amp raw",        s["moza.mb.amp"],  0x08E8)
    -- 0x1C28 / 65536 * 200 == 22.0 Hz exactly per EncodeFreq
    check("mb freq -> ~22 Hz", math.floor(0x1C28 / 65536.0 * 200.0 * 100 + 0.5) / 100, 22.0)
    -- Engine off: 7e 09 24 12 b1 04 00 00 00 00 00 00 00 7f
    local s2 = select(1, run(M.decode_mbooster, "7E092412" .. "B1040000" .. "00" .. "0000" .. "0000" .. "7F"))
    check("mb effect (Engine=4)", s2["moza.mb.effect"], 4)
    check("mb enable off",        s2["moza.mb.enable"], 0)
end

print("== group 0x35 — CM1 keyed value stream (docs/protocol/devices/dash-0x14.md)")
do
    -- Two 6-byte records: key 0xF54D = 25.0, key 0xDAA1 = 120.5
    -- 25.0   -> 0x41C80000 ; 120.5 -> 0x42F10000
    local s = select(1, run(M.decode_cm1, "7E0C3514" .. "F54D41C80000" .. "DAA142F10000" .. "00"))
    check("cm1 last key",   s["moza.cm1.key"], 0xDAA1)
    check("cm1 last value hex", s["moza.cm1.value.hex"], "42f10000")
end

print("== group 0x42 — FSR1 record (docs/protocol/devices/wheel-0x17.md)")
do
    -- Observed shape from usb-capture/fsr1: type 02, N=18, offsets 3-4 always 00
    local s = select(1, run(M.decode_fsr1,
        "7E124217" .. "02" .. "01" .. "40" .. "0000" .. "0102030405060708090A0B0C0D" .. "00"))
    check("fsr1 type", s["moza.fsr1.type"], 0x02)
    check("fsr1 b1",   s["moza.fsr1.b1"],   0x01)
    check("fsr1 b2",   s["moza.fsr1.b2"],   0x40)
    check("fsr1 data starts at offset 5", s["moza.fsr1.data.hex"], "0102030405060708090a0b0c0d")
end

print("== 0x43 7C 00 type 0x81 — session open (docs/protocol/sessions/type-0x81-channel-open.md)")
do
    -- 7E 0A 43 17 7C 00 01 81 01 00 01 00 FD 02 chk
    local s = select(1, run(M.decode_chunk, "7E0A4317" .. "7C00" .. "01" .. "81"
        .. "0100" .. "0100" .. "FD02" .. "00"))
    check("session id", s["moza.ss.session"], 0x01)
    check("chunk type", s["moza.ss.type"],    0x81)
    check("open seq",   s["moza.ss.seq"],     1)
    check("open port",  s["moza.ss.port"],    1)
    check("window",     s["moza.ss.window"],  765)
end

print("== 0x43 7C 00 type 0x00 — session close (docs/protocol/sessions/lifecycle.md)")
do
    local s = select(1, run(M.decode_chunk, "7E064317" .. "7C00" .. "02" .. "00" .. "0700" .. "00"))
    check("close session", s["moza.ss.session"], 0x02)
    check("close ack seq", s["moza.ss.ack_seq"], 7)
end

print("== 0x43 7C 00 type 0x01 — data chunk CRC (docs/protocol/sessions/chunk-format.md)")
do
    -- 4-byte net data "ABCD" + CRC32-LE. zlib.crc32(b"ABCD") = 0xDB1720A5
    local s = select(1, run(M.decode_chunk, "7E0E4317" .. "7C00" .. "04" .. "01" .. "1500"
        .. "41424344" .. "A52017DB" .. "00"))
    check("data seq",  s["moza.ss.seq"],  0x0015)
    check("net data",  s["moza.ss.data.hex"], "41424344")
    check("crc field", s["moza.ss.crc"], 0xDB1720A5)
    check("no bad-crc expert", s["expert:moza.expert.bad_crc"], nil)
    -- keepalive: zero net data, CRC of empty == 0
    local s2 = select(1, run(M.decode_chunk, "7E0A4317" .. "7C00" .. "06" .. "01" .. "0300"
        .. "00000000" .. "00"))
    check("keepalive crc ok", s2["expert:moza.expert.bad_crc"], nil)
end

print("== crc32 implementation")
do
    check("crc32('')",     M.crc32(TvbNew({})(0, 0)), 0x00000000)
    check("crc32('ABCD')", M.crc32(TvbNew(hexbytes("41424344"))(0, 4)), 0xDB1720A5)
    -- zlib.crc32(b"123456789") = 0xCBF43926
    check("crc32('123456789')",
        M.crc32(TvbNew(hexbytes("313233343536373839"))(0, 9)), 0xCBF43926)
end

print("== checksums (MozaProtocol.CalculateWireChecksum)")
do
    -- 7e 06 3f 17 1a 01 3d 3f 00 00 -> checksum 0x7E (docs/protocol/wire/checksum.md)
    local body = ba_new(hexbytes("3F171A013D3F0000"))
    check("wire chk = 0x7E", M.wire_checksum(0x06, body, 8), 0x7E)
    -- Legacy unstuffed telemetry frame from usb-capture/12-04-26-2/simhub-startup-1
    local raw = ba_new(hexbytes(
        "43177D233200233209207E04A60A1701000000004" .. "06A0000000041"))
    check("plain chk matches wire byte", M.plain_checksum(0x18, raw), 0x41)
end

print("== 0x43 7D 23 — live telemetry (docs/protocol/telemetry/live-stream.md)")
do
    -- 7E 08 43 17 7D 23 32 00 23 32 00 20 chk = empty-tier stub (N=8)
    local s = select(1, run(M.decode_live_telemetry,
        "7E084317" .. "7D23" .. "32002332" .. "00" .. "20" .. "00"))
    check("stub const4", s["moza.telem.const4.hex"], "32002332")
    check("stub flag",   s["moza.telem.flag"], 0x00)
    check("stub const20", s["moza.telem.const20"], 0x20)
    -- 16-byte F1 level-30 frame: N = 8 + 16 = 24
    local s2 = select(1, run(M.decode_live_telemetry,
        "7E184317" .. "7D23" .. "32002332" .. "02" .. "20"
        .. "000102030405060708090A0B0C0D0E0F" .. "00"))
    check("live flag", s2["moza.telem.flag"], 0x02)
    check("live data length", #(s2["moza.telem.live.hex"] or ""), 32)
end

print("== command-name lookup (longest prefix wins)")
do
    local function look(group, hex)
        local bytes = hexbytes(hex)
        return M.cmd_lookup(group, TvbNew(bytes), 0, #bytes)
    end
    check("0x43 7d23",      look(0x43, "7D2332"), "live-telemetry")
    check("0xC3 7c00",      look(0xC3, "7C0004"), "session chunk")
    check("0x3F 1a00",      look(0x3F, "1A00FF03"), "live-bitmask g0/RPM")
    check("0x3F 1f01ff03",  look(0x3F, "1F01FF03"), "led-color g1/button")
    check("0x40 2802",      look(0x40, "280201"), "set-multi-channel-mode")
    check("0x29 2e",        look(0x29, "2E0005"), "gearshift-vibration")
    check("0x28 17",        look(0x28, "170000"), "max-angle")
    check("0x2B 04",        look(0x2B, "04"),     "mcu-temp")
    check("0x2D f531",      look(0x2D, "F5310000"), "sequence-counter")
    check("0x41 fdde",      look(0x41, "FDDE0000"), "send-telemetry (enable)")
    check("0x2A 4301",      look(0x2A, "430105"), "music-index-set")
    check("0x32 81",        look(0x32, "8100000011"), "fsr1 dashboard-select")
    check("0x64 0101",      look(0x64, "010100"), "slot1 probe")
    check("0x24 b1",        look(0x24, "B1010100"), "mBooster motor-write")
    check("unknown group",  look(0x77, "0102"), nil)
end

print(string.format("\n%d passed, %d failed", pass, fail))
os.exit(fail == 0 and 0 or 1)
