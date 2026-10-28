for i=1,256 do
  -- Real native TeX log + terminal output, in many small chunks.
  pcall(texio.write_nl,'term and log',string.rep('M',2048))
end
