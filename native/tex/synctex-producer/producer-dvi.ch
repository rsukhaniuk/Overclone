% Explicit opt-in DVI completion site. The historical private C callback name
% observes FILE error/flush/close state, not PDF syntax. Reuse that checked sink
% transition on the ACTUAL DVI file, only after its native postamble is written.
@x
  b_close(dvi_file);
@y
  oc_synctex_pdf_close(dvi_file);
@z
