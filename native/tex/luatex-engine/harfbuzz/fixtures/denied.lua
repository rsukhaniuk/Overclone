for _, entry in ipairs({{"os", "execute"}, {"os", "exec"}, {"os", "spawn"},
    {"os", "exit"}, {"io", "popen"}, {"package", "loadlib"}}) do
  texio.write_nl("LUAHB-CAPABILITY=" .. entry[1] .. "." .. entry[2] .. ":" .. type(_G[entry[1]][entry[2]]))
end
assert(os.execute == nil and os.exec == nil and os.spawn == nil)
assert(status.shell_escape == 0)
-- luatex-core restores permission-checking/no-op wrappers after native startup.
-- Exercise their refusal rather than confusing a function value with authority.
assert(io.popen("R1_DENIED_SYNTHETIC_COMMAND") == nil)
assert(package.loadlib("R1_DENIED_SYNTHETIC_LIBRARY.so", "R1_DENIED") == nil)
for _, searcher in ipairs(package.searchers) do
  local ok, loader = pcall(searcher, "R1_DENIED_NATIVE_MODULE")
  assert(not ok or type(loader) ~= "function", "unexpected native loader authority")
end
for _, name in ipairs({"ffi", "socket", "socket.core", "mime.core"}) do
  local ok = pcall(require, name)
  assert(not ok, "forbidden native module loaded: " .. name)
end
texio.write_nl("LUAHB-DENIALS-PASS")
