% Owned hook: reached only after the actual pass writes trailer, flushes and
% finishes PDF. Checked closer observes stream errors before closing it.
@x
    if fixed_pdf_draftmode = 0 then b_close(pdf_file)
@y
    if fixed_pdf_draftmode = 0 then oc_synctex_pdf_close(pdf_file)
@z
