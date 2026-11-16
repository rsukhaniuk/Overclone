/* Actual native writer-channel validation only. Raw TeX/Lua literal/program,
 * forms, generic objects, inherited dictionaries and unsafe transforms keep
 * their existing refusal. This never clears the origin/geometry unsafe latch. */
#include "lua-neutral.h"
#include "writer_geometry.h"
#include <cmath>
#include <cstring>

namespace {
int role=0;
bool token_seen=false,color=false,color_seen=false;
void bad(){oc_lua_geometry_unsafe();}
bool white(char c){return c==' '||c=='\t'||c=='\r'||c=='\n'||c=='\f';}
struct Parser {
    const char *p,*end;
    void skip(){while(p<end&&white(*p))++p;}
    bool take(const char *s){
        skip();size_t n=std::strlen(s);
        if(static_cast<size_t>(end-p)<n||std::memcmp(p,s,n))return false;
        if(s[0]=='/'&&p+n<end&&!white(p[n])&&!std::strchr("()<>[]{}/%",p[n]))return false;
        p+=n;return true;
    }
    bool number(double &value){
        skip();bool minus=false,digit=false,dot=false;
        if(p<end&&(*p=='-'||*p=='+'))minus=*p++=='-';
        value=0;
        while(p<end&&*p>='0'&&*p<='9'){digit=true;value=value*10+(*p++-'0');if(value>1000000)return false;}
        if(p<end&&*p=='.'){dot=true;++p;double factor=.1;while(p<end&&*p>='0'&&*p<='9'){digit=true;value+=(*p++-'0')*factor;factor*=.1;}}
        (void)dot;if(minus)value=-value;
        return digit&&std::isfinite(value)&&(p==end||white(*p)||std::strchr("[]/",*p));
    }
    bool string(){
        skip();if(p==end||*p++!='(')return false;unsigned depth=1;
        while(p<end){char c=*p++;if(c=='\\'){if(p==end)return false;if(*p++=='\r'&&p<end&&*p=='\n')++p;}
            else if(c=='('){if(++depth>16)return false;}else if(c==')'&&!--depth)return true;}
        return false;
    }
    bool array(unsigned fixed,bool unit){
        if(!take("["))return false;unsigned n=0;
        for(;;){skip();if(p==end)return false;if(*p==']'){++p;return fixed?n==fixed:(n==1||n==3||n==4);}
            double x;if(++n>4||!number(x)||x<0||(unit&&x>1))return false;}
    }
    bool action(){
        if(!take("<<"))return false;unsigned seen=0;
        for(;;){skip();if(take(">>"))return (seen&6)==6;
            unsigned bit=0;
            if(take("/Type")){bit=1;if(!take("/Action"))return false;}
            else if(take("/S")){bit=2;if(!take("/URI"))return false;}
            else if(take("/URI")){bit=4;if(!string())return false;}
            else return false;
            if(seen&bit)return false;seen|=bit;
        }
    }
    bool tokens(int kind){
        unsigned seen=0,count=0;
        for(;;){skip();if(p==end)return kind==2?(seen&3)==3:true;if(++count>8)return false;unsigned bit=0;
            if(kind==3){
                bit=1;if(!take("/PageMode")||!(take("/UseNone")||take("/UseOutlines")||take("/UseThumbs")))return false;
            }else if(kind==2&&take("/Subtype")){bit=1;if(!take("/Link"))return false;}
            else if(kind==2&&take("/A")){bit=2;if(!action())return false;}
            else if(take("/Border")){bit=4;if(!array(3,false))return false;}
            else if(take("/H")){bit=8;if(!(take("/I")||take("/N")||take("/O")||take("/P")))return false;}
            else if(take("/C")){bit=16;if(!array(0,true))return false;}
            else return false;
            if(seen&bit)return false;seen|=bit;
        }
    }
    bool colors(){
        unsigned ops=0;
        for(;;){skip();if(p==end)return ops>0;unsigned n=0;double x;
            while(p<end&&(*p=='+'||*p=='-'||*p=='.'||(*p>='0'&&*p<='9'))){if(++n>4||!number(x)||x<0||x>1)return false;skip();}
            if(p==end||++ops>32)return false;
            unsigned want=0;
            if(end-p>=2&&((p[0]=='r'&&p[1]=='g')||(p[0]=='R'&&p[1]=='G'))){p+=2;want=3;}
            else if(*p=='g'||*p=='G'){++p;want=1;}
            else if(*p=='k'||*p=='K'){++p;want=4;}
            else return false;
            if(n!=want||(p<end&&!white(*p)))return false;
        }
    }
};
bool bytes(const char *p,size_t n){return p&&n&&n<=4096&&!std::memchr(p,0,n);}
}
extern "C" int oc_lua_neutral_abi(void){return 1;}
extern "C" void oc_lua_neutral_tokens_begin(int value){
    if(role||color||value<1||value>3){bad();return;}role=value;token_seen=false;
}
extern "C" int oc_lua_neutral_tokens(const char *p,size_t n){
    if(!role||token_seen||!bytes(p,n))return 0;token_seen=true;Parser parser{p,p+n};return parser.tokens(role)?1:0;
}
extern "C" void oc_lua_neutral_tokens_end(void){if(!role)bad();role=0;token_seen=false;}
extern "C" void oc_lua_neutral_color_begin(void){if(color||role){bad();return;}color=true;color_seen=false;}
extern "C" int oc_lua_neutral_color_pending(void){return color&&!color_seen;}
extern "C" int oc_lua_neutral_color_program(const char *p,size_t n,int safe){
    if(!color||color_seen||!safe||!bytes(p,n))return 0;color_seen=true;Parser parser{p,p+n};return parser.colors()?1:0;
}
extern "C" void oc_lua_neutral_color_end(void){if(!color||!color_seen)bad();color=false;color_seen=false;}
