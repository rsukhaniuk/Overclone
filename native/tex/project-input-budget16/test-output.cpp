// Unit witness for the actual unchanged wrapper callback paths. This test binary
// stubs only the converter producer; it is never a runtime candidate or PDF proof.
#include QUOTA_WRAPPER_PATH
#include <cassert>
static int mode;
extern "C" int dvipdfmx_simple_main(tt_bridge_api_t* api,char*,char*,bool,bool,time_t) {
    if(mode==3){
        void* in=api->input_open(nullptr,"input.bin",TTIF_BINARY,0);assert(in);
        assert(api->input_get_size(nullptr,in)==16777216);
        std::vector<char> chunk(65536);size_t total=0;ssize_t got;
        while((got=api->input_read(nullptr,in,chunk.data(),chunk.size()))>0)total+=got;
        assert(total==16777216);assert(api->input_getc(nullptr,in)==EOF);
        assert(api->input_close(nullptr,in)==0);
    }
    void* out=api->output_open(nullptr,"result.pdf",0);assert(out);
    std::vector<char> bytes(8388609,'x');memcpy(bytes.data(),"%PDF-1.7",8);
    if(mode==0){assert(api->output_write(nullptr,out,bytes.data(),8388608)==8388608);assert(api->output_flush(nullptr,out)==0);assert(api->output_close(nullptr,out)==0);}
    else if(mode==1){assert(api->output_write(nullptr,out,bytes.data(),8388607)==8388607);assert(api->output_putc(nullptr,out,'x')=='x');assert(api->output_putc(nullptr,out,'x')==EOF);assert(api->output_write(nullptr,out,"x",1)==0);assert(api->output_flush(nullptr,out)==-1);assert(api->output_close(nullptr,out)==-1);}
    else if(mode==2){assert(api->output_write(nullptr,out,bytes.data(),8388609)==0);assert(api->output_write(nullptr,out,"x",1)==0);assert(api->output_flush(nullptr,out)==-1);assert(api->output_close(nullptr,out)==-1);}
    else{assert(api->output_write(nullptr,out,bytes.data(),16)==16);assert(api->output_close(nullptr,out)==0);}
    return 0;
}
int main(int argc,char** argv){
    assert(argc==2);mode=atoi(argv[1]);std::vector<uint8_t> input(mode==3?16777216:1,'a');
    assert(oc_dvi_add_file("input.bin",input.data(),input.size(),0)==0);
    assert(oc_dvi_input_budget_abi()==1&&oc_dvi_project_input_limit()==16777216&&oc_dvi_pdf_output_limit()==8388608);
    int rc=oc_dvi_run("input.bin");
    assert(rc==((mode==1||mode==2)?2:0));assert(oc_dvi_status()==rc);
    assert(oc_dvi_pdf_size()==(mode==0?8388608:mode==3?16:0));
    if(mode==1||mode==2){assert(oc_dvi_pdf_data()==nullptr);assert(oc_dvi_geometry_size()==0);assert(limited);}
    assert(oc_dvi_run("input.bin")==3);assert(oc_dvi_pdf_size()==0);
    printf("actual callback boundary mode%d passed\n",mode);
}
