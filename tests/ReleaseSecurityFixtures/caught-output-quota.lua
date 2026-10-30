-- SYNTHETIC. Only selected-job virtual output; no external/host pathname.
-- Fixed 64KiB chunk, at most129 writes:8MiB+64KiB logical output, never OOM.
-- Root uses a Lua selected root whose generated job name is result.
local ok = pcall(function()
    local output = assert(io.open("result.aux", "wb"))
    local chunk = string.rep("Q", 65536)
    for _ = 1, 129 do assert(output:write(chunk)) end
    assert(output:close())
end)
-- Intentionally continue even when an engine-side IO quota was caught.
-- A latched failure MUST NOT promote this apparent success or partial output.
tex.print("R4_SYNTHETIC_AFTER_CAUGHT_QUOTA_" .. tostring(ok))
