#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include "../native/memory_core.h"
#include "../native/trace_core.h"
#include <algorithm>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <functional>
#include <limits>
#include <stdexcept>
#include <string>
#include <thread>
#include <vector>

static void check(bool ok, const char* text) { if (!ok) throw std::runtime_error(text); }
struct Core {
    HMODULE dll;
    decltype(&ms_open) open; decltype(&ms_close) close; decltype(&ms_scan_ex) scan;
    decltype(&ms_scan) legacy; decltype(&ms_undo_scan) undo; decltype(&ms_get_scan_history) history;
    decltype(&ms_get_results) results; decltype(&ms_result_count) count; decltype(&ms_write) write;
    decltype(&ms_read) read; decltype(&ms_write_code) code; decltype(&ms_get_progress) progress;
    decltype(&ms_cancel) cancel; decltype(&ms_error) error;
    decltype(&ms_trace_start) trace_start; decltype(&ms_trace_stop) trace_stop;
    decltype(&ms_trace_close) trace_close; decltype(&ms_trace_get_state) trace_state;
    template<class T> T symbol(const char* name) { auto p = GetProcAddress(dll, name); check(p != nullptr, name); return reinterpret_cast<T>(p); }
    Core(const wchar_t* path) : dll(LoadLibraryW(path)) {
        check(dll != nullptr, "LoadLibrary");
        open=symbol<decltype(open)>("ms_open"); close=symbol<decltype(close)>("ms_close"); scan=symbol<decltype(scan)>("ms_scan_ex");
        legacy=symbol<decltype(legacy)>("ms_scan"); undo=symbol<decltype(undo)>("ms_undo_scan"); history=symbol<decltype(history)>("ms_get_scan_history");
        results=symbol<decltype(results)>("ms_get_results"); count=symbol<decltype(count)>("ms_result_count"); write=symbol<decltype(write)>("ms_write");
        read=symbol<decltype(read)>("ms_read"); code=symbol<decltype(code)>("ms_write_code"); progress=symbol<decltype(progress)>("ms_get_progress");
        cancel=symbol<decltype(cancel)>("ms_cancel"); error=symbol<decltype(error)>("ms_error");
        trace_start=symbol<decltype(trace_start)>("ms_trace_start"); trace_stop=symbol<decltype(trace_stop)>("ms_trace_stop");
        trace_close=symbol<decltype(trace_close)>("ms_trace_close"); trace_state=symbol<decltype(trace_state)>("ms_trace_get_state");
    }
    ~Core() { FreeLibrary(dll); }
};
struct Child {
    PROCESS_INFORMATION info{};
    Child() {
        wchar_t path[32768]{}; check(GetModuleFileNameW(nullptr,path,32768)!=0,"GetModuleFileName");
        std::wstring args=L"\""+std::wstring(path)+L"\" --child";
        STARTUPINFOW startup{}; startup.cb=sizeof(startup);
        check(CreateProcessW(path,args.data(),nullptr,nullptr,FALSE,CREATE_NO_WINDOW,nullptr,nullptr,&startup,&info)!=0,"Create owned child");
    }
    ~Child() { TerminateProcess(info.hProcess,0); WaitForSingleObject(info.hProcess,5000); CloseHandle(info.hThread); CloseHandle(info.hProcess); }
};
struct Memory {
    HANDLE process; uint64_t address; size_t size;
    Memory(HANDLE p,size_t bytes=4096):process(p),size(bytes) { address=reinterpret_cast<uint64_t>(VirtualAllocEx(p,nullptr,bytes,MEM_RESERVE|MEM_COMMIT,PAGE_READWRITE)); check(address!=0,"VirtualAllocEx"); }
    ~Memory(){ VirtualFreeEx(process,reinterpret_cast<LPVOID>(address),0,MEM_RELEASE); }
    void put(size_t offset,const void* data,size_t bytes){ SIZE_T written=0; check(WriteProcessMemory(process,reinterpret_cast<LPVOID>(address+offset),data,bytes,&written)&&written==bytes,"Seed child memory"); }
    template<class T> void put(size_t offset,T value){ put(offset,&value,sizeof(value)); }
};
struct Session {
    Core& core; void* handle;
    Session(Core& c,DWORD pid):core(c),handle(c.open(pid)){check(handle!=nullptr,"Open child session");}
    ~Session(){core.close(handle);}
    void ok(ms_scan_request& r,bool next=false,ms_scan_options* options=nullptr){int status=core.scan(handle,&r,next,options); if(status!=MS_OK){ char error[512]{};core.error(handle,error,512);throw std::runtime_error(error);} }
    void expect(std::initializer_list<uint64_t> wanted){ std::vector<uint64_t> actual(static_cast<size_t>(core.count(handle))); if(!actual.empty())core.results(handle,0,actual.data(),static_cast<uint32_t>(actual.size()));check(actual==std::vector<uint64_t>(wanted),"Unexpected child result addresses"); }
    ms_scan_history_info history(){ms_scan_history_info info{};core.history(handle,&info);return info;}
};
static ms_scan_request request(Memory& memory,uint32_t type,uint32_t mode,const void* value,uint32_t width,uint32_t alignment=1,size_t start=0,size_t size=0){
    ms_scan_request r{};r.type=type;r.mode=mode;r.start_address=memory.address+start;r.end_address=r.start_address+(size?size:memory.size);
    r.value=static_cast<const uint8_t*>(value);r.value_size=width;r.alignment=alignment;r.writable_only=1;return r;
}

int wmain(int argc,wchar_t** argv){
    if(argc>1&&std::wcscmp(argv[1],L"--child")==0){Sleep(60000);return 0;}
    int passed=0,failed=0;
    try{
        Core core(argc>1?argv[1]:L"artifacts\\native-expanded-check\\memory_core.dll");Child child;
        auto run=[&](const char* name,const std::function<void()>& work){try{work();++passed;std::printf("PASS %s\n",name);}catch(const std::exception& e){++failed;std::printf("FAIL %s: %s\n",name,e.what());}};
        run("ABI stays 56 bytes; extensions have fixed sizes",[&]{check(sizeof(ms_scan_request)==56&&sizeof(ms_scan_options)==56&&sizeof(ms_scan_history_info)==48,"ABI mismatch");});
        run("Greater/less/inclusive-range exact endpoints in a real child",[&]{
            Memory m(child.info.hProcess);int32_t values[]={50,100,150};m.put(64,values,sizeof(values));int32_t value=100;
            Session s(core,child.info.dwProcessId);auto r=request(m,MS_I32,MS_GREATER_THAN,&value,4,4,64,12);s.ok(r);s.expect({m.address+72});
            r.mode=MS_LESS_THAN;s.ok(r);s.expect({m.address+64});
            int32_t upper=150;ms_scan_options options{};options.struct_size=sizeof(options);options.upper_value=reinterpret_cast<uint8_t*>(&upper);options.upper_value_size=4;
            r.mode=MS_BETWEEN;s.ok(r,false,&options);s.expect({m.address+68,m.address+72});
            upper=99;auto before=s.history();check(core.scan(s.handle,&r,0,&options)==MS_INVALID,"Reversed range accepted");check(s.history().generation==before.generation,"Invalid range mutated history");s.expect({m.address+68,m.address+72});
        });
        run("Specified deltas and undo restore comparison snapshots",[&]{
            Memory m(child.info.hProcess);int32_t values[]={50,100,150};m.put(64,values,sizeof(values));Session s(core,child.info.dwProcessId);
            auto r=request(m,MS_I32,MS_UNKNOWN,nullptr,0,4,64,12);s.ok(r);values[0]=55;values[1]=110;values[2]=145;m.put(64,values,sizeof(values));
            int32_t delta=5;r.mode=MS_INCREASED_BY;r.value=reinterpret_cast<uint8_t*>(&delta);r.value_size=4;s.ok(r,true);s.expect({m.address+64});
            check(core.undo(s.handle)==MS_OK,"Undo increased-by");s.expect({m.address+64,m.address+68,m.address+72});
            r.mode=MS_DECREASED_BY;s.ok(r,true);s.expect({m.address+72});
            check(core.undo(s.handle)==MS_OK,"Undo decreased-by");r.mode=MS_UNCHANGED;r.value=nullptr;r.value_size=0;s.ok(r,true);s.expect({});
            check(core.undo(s.handle)==MS_OK,"Undo unchanged");check(core.undo(s.handle)==MS_OK,"Undo initial");check(!s.history().has_scan,"Undo initial did not restore no-scan state");
            check(core.scan(s.handle,&r,1,nullptr)==MS_INVALID,"Next scan after undo to empty succeeded");
        });
        run("Int64 specified-delta overflow and negative delta rejection",[&]{
            Memory m(child.info.hProcess);int64_t high=INT64_MAX,low=INT64_MIN,delta=1;m.put(64,high);m.put(72,low);Session s(core,child.info.dwProcessId);
            auto r=request(m,MS_I64,MS_UNKNOWN,nullptr,0,8,64,16);s.ok(r);m.put(64,low);m.put(72,high);r.mode=MS_INCREASED_BY;r.value=reinterpret_cast<uint8_t*>(&delta);r.value_size=8;s.ok(r,true);s.expect({});
            check(core.undo(s.handle)==MS_OK,"Undo overflow filter");r.mode=MS_DECREASED_BY;s.ok(r,true);s.expect({});
            delta=-1;check(core.scan(s.handle,&r,1,nullptr)==MS_INVALID,"Negative delta accepted");
        });
        run("Float absolute/relative tolerances and finite validation",[&]{
            Memory m(child.info.hProcess);float values[]={1.0005f,1.01f};m.put(64,values,sizeof(values));float value=1.0f;Session s(core,child.info.dwProcessId);
            auto r=request(m,MS_F32,MS_EXACT,&value,4,4,64,8);ms_scan_options options{};options.struct_size=sizeof(options);options.flags=MS_APPROXIMATE;options.absolute_tolerance=0.001;
            s.ok(r,false,&options);s.expect({m.address+64});options.absolute_tolerance=-1;check(core.scan(s.handle,&r,0,&options)==MS_INVALID,"Negative tolerance accepted");options.absolute_tolerance=0;
            double doubles[]={1000.5,1005};double wanted=1000;m.put(128,doubles,sizeof(doubles));r=request(m,MS_F64,MS_EXACT,&wanted,8,8,128,16);options.relative_tolerance=0.001;s.ok(r,false,&options);s.expect({m.address+128});
            m.put(128,1000.6);r.mode=MS_UNCHANGED;r.value=nullptr;r.value_size=0;s.ok(r,true,&options);s.expect({m.address+128});
            options.relative_tolerance=std::numeric_limits<double>::infinity();check(core.scan(s.handle,&r,1,&options)==MS_INVALID,"Infinite tolerance accepted");
        });
        run("AOB full/half-byte wildcard crossing 1MiB block boundary",[&]{
            Memory m(child.info.hProcess,1024*1024+4096);const uint8_t actual[]={0x48,0x8B,0x72,0x9F,0x11};m.put(1024*1024-2,actual,5);m.put(64,actual,5);
            const uint8_t value[]={0x48,0x8B,0,0x0F,0x10},mask[]={0xFF,0xFF,0,0x0F,0xF0};Session s(core,child.info.dwProcessId);
            auto r=request(m,MS_BYTES,MS_EXACT,value,5);ms_scan_options options{};options.struct_size=sizeof(options);options.pattern_mask=mask;options.pattern_mask_size=5;
            s.ok(r,false,&options);s.expect({m.address+64,m.address+1024*1024-2});
            options.pattern_mask_size=4;auto before=s.history();check(core.scan(s.handle,&r,0,&options)==MS_INVALID,"Mismatched mask accepted");check(before.generation==s.history().generation,"Rejected mask changed history");
        });
        run("History restores type/width and bounded step count",[&]{
            Memory m(child.info.hProcess);int32_t value=0;Session s(core,child.info.dwProcessId);auto r=request(m,MS_I32,MS_EXACT,&value,4,4,64,4);s.ok(r);
            uint8_t needle=0;r=request(m,MS_U8,MS_EXACT,&needle,1,1,64,1);s.ok(r);check(core.undo(s.handle)==MS_OK,"Undo type reset");check(s.history().type==MS_I32&&s.history().byte_width==4,"Type/width metadata not restored");
            for(int i=0;i<20;++i)s.ok(r);auto info=s.history();check(info.undo_count==16&&info.used_bytes<=info.budget_bytes,"History not bounded");
        });
        run("History budget uses allocated candidate+snapshot memory",[&]{
            Memory m(child.info.hProcess,2'000'000);Session s(core,child.info.dwProcessId);uint8_t zero=0;auto r=request(m,MS_U8,MS_EXACT,&zero,1);
            for(int i=0;i<6;++i)s.ok(r);auto info=s.history();check(info.undo_count<6&&info.used_bytes<=64*1024*1024,"History memory cap not enforced");check(core.undo(s.handle)==MS_OK&&core.count(s.handle)==2'000'000,"Budgeted undo lost real candidates");
        });
        run("Cancelled extended scan preserves candidates/snapshot/history",[&]{
            Memory m(child.info.hProcess);Memory large(child.info.hProcess,128*1024*1024);int32_t value=42;m.put(64,value);Session s(core,child.info.dwProcessId);auto baseline=request(m,MS_I32,MS_EXACT,&value,4,4,64,4);s.ok(baseline);
            auto before=s.history();m.put(64,int32_t{43});uint8_t needle=0xF7;auto r=request(large,MS_U8,MS_EXACT,&needle,1);int status=-1;
            std::thread worker([&]{status=core.scan(s.handle,&r,0,nullptr);});bool observed=false;for(int i=0;i<1000;++i){ms_progress p{};core.progress(s.handle,&p);if(p.running){core.cancel(s.handle);observed=true;break;}Sleep(1);}worker.join();
            check(observed&&status==MS_CANCELLED,"Cancellation did not interrupt extended scan");check(s.history().generation==before.generation&&s.history().undo_count==before.undo_count,"Cancelled scan entered history");s.expect({m.address+64});
            baseline.mode=MS_CHANGED;baseline.value=nullptr;baseline.value_size=0;s.ok(baseline,true);s.expect({m.address+64});
        });
        run("Explicit code write restores distinct page protections",[&]{
            Memory m(child.info.hProcess,8192);DWORD old=0;check(VirtualProtectEx(child.info.hProcess,reinterpret_cast<LPVOID>(m.address),4096,PAGE_EXECUTE_READ,&old)!=0,"Protect RX");check(VirtualProtectEx(child.info.hProcess,reinterpret_cast<LPVOID>(m.address+4096),4096,PAGE_READONLY,&old)!=0,"Protect R");
            Session s(core,child.info.dwProcessId);const uint8_t bytes[]={0x90,0xC3};check(core.write(s.handle,m.address+4095,bytes,2)==MS_ACCESS,"Ordinary write altered readonly code");check(core.code(s.handle,m.address+4095,bytes,2)==MS_OK,"Explicit code write failed");
            uint8_t got[2]{};check(core.read(s.handle,m.address+4095,got,2)==MS_OK&&std::memcmp(got,bytes,2)==0,"Code patch bytes mismatch");MEMORY_BASIC_INFORMATION first{},second{};VirtualQueryEx(child.info.hProcess,reinterpret_cast<LPCVOID>(m.address),&first,sizeof(first));VirtualQueryEx(child.info.hProcess,reinterpret_cast<LPCVOID>(m.address+4096),&second,sizeof(second));check(first.Protect==PAGE_EXECUTE_READ&&second.Protect==PAGE_READONLY,"Original protections not restored");
        });
        run("Modeless tracing excludes watched pages on every native handle",[&]{
            Memory m(child.info.hProcess,8192);int32_t value=100;m.put(64,value);m.put(4096+64,value);Session s(core,child.info.dwProcessId),other(core,child.info.dwProcessId);
            auto unknown=request(m,MS_I32,MS_UNKNOWN,nullptr,0,4);other.ok(unknown);
            check(core.count(other.handle)==2048,"Unknown baseline before tracing");
            void* trace=core.trace_start(child.info.dwProcessId,m.address+64,4,MS_TRACE_ACCESS);check(trace!=nullptr,"Trace attach");
            try {
                check(core.trace_start(child.info.dwProcessId,m.address+64,4,MS_TRACE_WRITE)==nullptr,"Second same-PID trace accepted");
                int32_t got=0;check(core.read(s.handle,m.address+64,reinterpret_cast<uint8_t*>(&got),4)==MS_BUSY,"Read consumed trace guard");
                check(core.read(other.handle,m.address+64,reinterpret_cast<uint8_t*>(&got),4)==MS_BUSY,"Independent handle consumed trace guard");
                check(core.write(s.handle,m.address+64,reinterpret_cast<uint8_t*>(&value),4)==MS_BUSY,"Write altered traced page");
                check(core.code(s.handle,m.address+64,reinterpret_cast<uint8_t*>(&value),4)==MS_BUSY,"Code patch altered traced page");
                check(core.read(s.handle,m.address+4096+64,reinterpret_cast<uint8_t*>(&got),4)==MS_OK&&got==100,"Unrelated-page read blocked");
                auto r=request(m,MS_I32,MS_EXACT,&value,4,4);s.ok(r);s.expect({m.address+4096+64});
                auto unchanged=unknown;unchanged.mode=MS_UNCHANGED;other.ok(unchanged,true);
                uint64_t first=0;check(core.count(other.handle)==1024&&core.results(other.handle,0,&first,1)==1&&first==m.address+4096,"Compact rescan touched the tracked page");
                ms_trace_state state{};core.trace_state(trace,&state);check(state.running&&state.status==MS_OK,"Unrelated browsing disturbed trace");
                // Simulate the guard-free rearm interval: registration still protects the page.
                DWORD old=0;check(VirtualProtectEx(child.info.hProcess,reinterpret_cast<LPVOID>(m.address),4096,PAGE_READWRITE,&old)!=0,"Simulate rearm gap");
                check(core.read(s.handle,m.address+64,reinterpret_cast<uint8_t*>(&got),4)==MS_BUSY,"Guard-free rearm interval leaked read");
                s.ok(r);s.expect({m.address+4096+64});
                s.ok(unknown);check(core.count(s.handle)==1024&&core.results(s.handle,0,&first,1)==1&&first==m.address+4096,"Unknown initial consumed the guard-free tracked page");
                s.ok(unchanged,true);check(core.count(s.handle)==1024,"Compact unchanged consumed the guard-free tracked page");
                core.trace_stop(trace);core.trace_close(trace);trace=nullptr;
                check(core.read(s.handle,m.address+64,reinterpret_cast<uint8_t*>(&got),4)==MS_OK,"Detached page remained reserved");
                check(core.undo(other.handle)==MS_OK&&core.count(other.handle)==2048,"Tracked-page filtering corrupted the retained unknown baseline");
            } catch (...) { core.trace_stop(trace); core.trace_close(trace); throw; }
        });
    }catch(const std::exception& e){std::fprintf(stderr,"SETUP FAILED %s\n",e.what());return 2;}
    std::printf("%d passed, %d failed\n",passed,failed);return failed?1:0;
}
