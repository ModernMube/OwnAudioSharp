/**
 * chord_detector.cpp
 *
 * Runtime dispatch:
 *   nmp.onnx found  →  BasicPitch ONNX inference → note events → chromagram → templates
 *   nmp.onnx absent →  HPCP (pure-DSP fallback, drums corrupt chromagram)
 *
 * BasicPitch constants mirror the C# Constants class in AudioReaderNote.cs.
 */

#include "../ownaudio_ml.h"
#include <cstring>
#include <cstdio>
#include <cmath>
#include <cstdint>
#include <vector>
#include <complex>
#include <string>
#include <algorithm>
#include <sys/stat.h>

#ifdef _WIN32
#  include <windows.h>
#endif

#ifdef OWNAUDIO_ML_HAS_ONNXRUNTIME
#  include <onnxruntime_c_api.h>

// BasicPitch constants (mirrors C# Constants class in AudioReaderNote.cs)
static const int   BP_FFT_HOP    = 256;
static const int   BP_SR         = 22050;
static const int   BP_N_SAMPLES  = BP_SR * 2 - BP_FFT_HOP;       // 43844
static const int   BP_N_OLAP_FR  = 30;
static const int   BP_OLAP_LEN   = BP_N_OLAP_FR * BP_FFT_HOP;    // 7680
static const int   BP_HOP_SIZE   = BP_N_SAMPLES - BP_OLAP_LEN;   // 36164
static const int   BP_ANNOT_FPS  = BP_SR / BP_FFT_HOP;           // 86
static const int   BP_ANN_FRAMES = BP_ANNOT_FPS * 2;             // 172
static const int   BP_MIDI_OFF   = 21;
static const int   BP_N_NOTES    = 88;
static const float BP_ONSET_THR  = 0.5f;
static const float BP_FRAME_THR  = 0.2f;
static const int   BP_MIN_NOTE   = 15;
static const int   BP_ENERGY_THR = 11;
static const float BP_MIN_FREQ   = 90.0f;
static const float BP_MAX_FREQ   = 2800.0f;
static const float k_win_sec     = 1.0f;

static bool path_exists(const char* p) { struct stat st; return stat(p,&st)==0; }

#endif  // forward-declare guard

// ── Chord catalogue ───────────────────────────────────────────────────────────
static const char* const k_notes[12] = {
    "C","C#","D","D#","E","F","F#","G","G#","A","A#","B"
};
struct ChordType { const char* sfx; int iv[4]; int n; };
static const ChordType k_types[6] = {
    {"",    {0,4, 7, 0}, 3},
    {"m",   {0,3, 7, 0}, 3},
    {"7",   {0,4, 7,10}, 4},
    {"m7",  {0,3, 7,10}, 4},
    {"maj7",{0,4, 7,11}, 4},
    {"dim", {0,3, 6, 0}, 3},
};
static const int k_NT = 6;
static const float k_min_conf   = 0.50f;
static const float k_hop_sec    = 0.5f;

// ── Shared helpers ────────────────────────────────────────────────────────────

static void build_templates(float t[12][6][12]) {
    for(int r=0;r<12;++r)
        for(int ti=0;ti<k_NT;++ti) {
            float* tp=t[r][ti];
            for(int i=0;i<12;++i) tp[i]=0;
            float w=1.0f/(float)k_types[ti].n;
            for(int i=0;i<k_types[ti].n;++i)
                tp[(r+k_types[ti].iv[i])%12]=w;
        }
}

static float cosine_sim(const float* a, const float* b) {
    float dot=0,ma=0,mb=0;
    for(int i=0;i<12;++i){dot+=a[i]*b[i];ma+=a[i]*a[i];mb+=b[i]*b[i];}
    float d=std::sqrt(ma)*std::sqrt(mb);
    return d>1e-8f?dot/d:0.0f;
}

static void best_chord(const float* ch, const float t[12][6][12],
                        int& br, int& bt, float& bs) {
    bs=-1; br=0; bt=0;
    for(int r=0;r<12;++r)
        for(int ti=0;ti<k_NT;++ti){
            float s=cosine_sim(ch,t[r][ti]);
            if(s>bs){bs=s;br=r;bt=ti;}
        }
}

static int emit_segments(
    const std::vector<int>& roots, const std::vector<int>& types,
    const std::vector<float>& confs, const std::vector<float>& times,
    OwnAudioMlChordResult* res, int max_res)
{
    int n=(int)roots.size(), cnt=0, seg=0;
    for(int f=1;f<=n;++f){
        bool end=(f==n)||(roots[f]!=roots[f-1]||types[f]!=types[f-1]);
        if(!end) continue;
        float avg=0;
        for(int k=seg;k<f;++k) avg+=confs[k];
        avg/=(f-seg);
        if(avg>=k_min_conf && cnt<max_res){
            auto& r=res[cnt++];
            r.start_time = times[seg];
            r.end_time   = (f<n) ? times[f] : times[f-1]+k_hop_sec;
            r.confidence = avg;
            std::snprintf(r.chord_name,sizeof(r.chord_name),
                          "%s%s",k_notes[roots[seg]],k_types[types[seg]].sfx);
        }
        seg=f;
    }
    return cnt;
}

// ── HPCP fallback path ────────────────────────────────────────────────────────
static void fft_inplace(std::complex<float>* x, int n) {
    for(int i=1,j=0;i<n;++i){
        int bit=n>>1;
        for(;j&bit;bit>>=1) j^=bit;
        j^=bit;
        if(i<j) std::swap(x[i],x[j]);
    }
    for(int len=2;len<=n;len<<=1){
        float ang=-2.0f*(float)M_PI/(float)len;
        std::complex<float> wlen(std::cos(ang),std::sin(ang));
        for(int i=0;i<n;i+=len){
            std::complex<float> w(1,0);
            for(int j=0;j<len/2;++j){
                auto u=x[i+j],v=x[i+j+len/2]*w;
                x[i+j]=u+v; x[i+j+len/2]=u-v; w*=wlen;
            }
        }
    }
}

static void hpcp(const std::vector<float>& mag, int fsz, int sr, float* out) {
    for(int i=0;i<12;++i) out[i]=0;
    float tot=0, bHz=(float)sr/(float)fsz;
    for(int b=1;b<fsz/2;++b){
        float f=b*bHz;
        if(f<27.5f||f>14080.f) continue;
        float m=mag[b]; if(m<1e-6f) continue;
        for(int h=1;h<=6;++h){
            float hf=f/(float)h; if(hf<27.5f) break;
            int pc=((int)std::round(12.f*std::log2f(hf/440.f))%12+12)%12;
            float w=m/(float)h; out[pc]+=w; tot+=w;
        }
    }
    if(tot>0) for(int i=0;i<12;++i) out[i]/=tot;
}

static int detect_hpcp(const float* in, int n, int sr,
                        OwnAudioMlChordResult* res, int maxr)
{
    float tmpl[12][6][12]; build_templates(tmpl);
    const int FSZ=4096, HOP=2048;
    std::vector<float> hann(FSZ);
    for(int i=0;i<FSZ;++i) hann[i]=0.5f*(1.f-std::cos(2.f*(float)M_PI*i/(FSZ-1)));
    std::vector<std::complex<float>> fb(FSZ);
    std::vector<float> mag(FSZ/2+1);
    int nf=(n-FSZ)/HOP+1; if(nf<=0) return 0;
    std::vector<int>   fr(nf),ft(nf); std::vector<float> fc(nf),ft2(nf);
    for(int f=0;f<nf;++f){
        int s=f*HOP;
        for(int i=0;i<FSZ;++i) fb[i]={in[s+i]*hann[i],0};
        fft_inplace(fb.data(),FSZ);
        for(int i=0;i<=FSZ/2;++i) mag[i]=std::abs(fb[i]);
        float ch[12]; hpcp(mag,FSZ,sr,ch);
        best_chord(ch,tmpl,fr[f],ft[f],fc[f]);
        ft2[f]=(float)(f*HOP)/(float)sr;
    }
    return emit_segments(fr,ft,fc,ft2,res,maxr);
}

// ══════════════════════════════════════════════════════════════════════════════
// BasicPitch / ONNX path  (compiled only when ORT is available)
// ══════════════════════════════════════════════════════════════════════════════
#ifdef OWNAUDIO_ML_HAS_ONNXRUNTIME

static const OrtApi* g_ort         = nullptr;
static OrtEnv*        g_env         = nullptr;
static OrtSession*    g_nmp_session = nullptr;
static std::string    g_iname;                        // input tensor name
static std::vector<std::string> g_onames;             // output names (sorted a-z)

// ── Lifecycle ─────────────────────────────────────────────────────────────────
void chord_detector_try_init(const char* model_dir)
{
    std::string path = model_dir;
    if(!path.empty() && path.back()!='/' && path.back()!='\\') path+='/';
    path += "nmp.onnx";
    if(!path_exists(path.c_str())) return;

    g_ort = OrtGetApiBase()->GetApi(ORT_API_VERSION);
    if(!g_ort) return;

    if(g_ort->CreateEnv(ORT_LOGGING_LEVEL_WARNING,"cd",&g_env)) { g_env=nullptr; return; }

    OrtSessionOptions* opts=nullptr;
    if(g_ort->CreateSessionOptions(&opts)) return;
    g_ort->SetIntraOpNumThreads(opts,2);
    g_ort->SetInterOpNumThreads(opts,1);

    OrtStatus* st=nullptr;
#ifdef _WIN32
    int wn=MultiByteToWideChar(CP_UTF8,0,path.c_str(),-1,nullptr,0);
    std::vector<wchar_t> wp(wn);
    MultiByteToWideChar(CP_UTF8,0,path.c_str(),-1,wp.data(),wn);
    st=g_ort->CreateSession(g_env,wp.data(),opts,&g_nmp_session);
#else
    st=g_ort->CreateSession(g_env,path.c_str(),opts,&g_nmp_session);
#endif
    g_ort->ReleaseSessionOptions(opts);
    if(st){ g_ort->ReleaseStatus(st); g_nmp_session=nullptr; return; }

    OrtAllocator* al=nullptr;
    g_ort->GetAllocatorWithDefaultOptions(&al);

    char* tmp=nullptr;
    g_ort->SessionGetInputName(g_nmp_session,0,al,&tmp);
    g_iname=tmp; al->Free(al,tmp);

    size_t no=0; g_ort->SessionGetOutputCount(g_nmp_session,&no);
    std::vector<std::pair<std::string,size_t>> ons(no);
    for(size_t i=0;i<no;++i){
        g_ort->SessionGetOutputName(g_nmp_session,i,al,&tmp);
        ons[i]={tmp,i}; al->Free(al,tmp);
    }
    std::sort(ons.begin(),ons.end());          // alphabetical → [0]=contour,[1]=note,[2]=onset
    g_onames.resize(no);
    for(size_t i=0;i<no;++i) g_onames[i]=ons[i].first;
}

void chord_detector_shutdown_ort()
{
    if(g_nmp_session){ g_ort->ReleaseSession(g_nmp_session); g_nmp_session=nullptr; }
    if(g_env)        { g_ort->ReleaseEnv(g_env);              g_env=nullptr; }
    g_ort=nullptr; g_iname.clear(); g_onames.clear();
}

// ── Linear resampler to 22050 Hz ─────────────────────────────────────────────
static std::vector<float> resample(const float* in, int n, int sr)
{
    if(sr==BP_SR) return {in,in+n};
    double r=(double)BP_SR/sr;
    int m=(int)(n*r);
    std::vector<float> out(m);
    for(int i=0;i<m;++i){
        double si=i/r;
        int i0=(int)si, i1=std::min(i0+1,n-1);
        double f=si-i0;
        out[i]=(float)(in[i0]*(1-f)+in[i1]*f);
    }
    return out;
}

// ── Run one window through the model ─────────────────────────────────────────
struct WinOut { std::vector<float> notes, onsets; };  // [ANN_FRAMES × N_NOTES] each

static bool run_window(const float* win, WinOut& wo)
{
    OrtMemoryInfo* mem=nullptr;
    if(g_ort->CreateCpuMemoryInfo(OrtArenaAllocator,OrtMemTypeDefault,&mem)) return false;
    int64_t sh[]={1,(int64_t)BP_N_SAMPLES};
    OrtValue* it=nullptr;
    OrtStatus* st=g_ort->CreateTensorWithDataAsOrtValue(
        mem,const_cast<float*>(win),BP_N_SAMPLES*sizeof(float),
        sh,2,ONNX_TENSOR_ELEMENT_DATA_TYPE_FLOAT,&it);
    g_ort->ReleaseMemoryInfo(mem);
    if(st){ g_ort->ReleaseStatus(st); return false; }

    size_t no=g_onames.size();
    std::vector<const char*> onames(no);
    for(size_t i=0;i<no;++i) onames[i]=g_onames[i].c_str();
    std::vector<OrtValue*> ot(no,nullptr);
    const char* iname=g_iname.c_str();
    st=g_ort->Run(g_nmp_session,nullptr,&iname,&it,1,onames.data(),no,ot.data());
    g_ort->ReleaseValue(it);
    if(st){ g_ort->ReleaseStatus(st); return false; }

    bool ok=no>=3;
    if(ok){
        float *np=nullptr,*op=nullptr;
        g_ort->GetTensorMutableData(ot[1],(void**)&np);   // note
        g_ort->GetTensorMutableData(ot[2],(void**)&op);   // onset
        int sz=BP_ANN_FRAMES*BP_N_NOTES;
        wo.notes .assign(np,np+sz);
        wo.onsets.assign(op,op+sz);
    }
    for(auto* t:ot) if(t) g_ort->ReleaseValue(t);
    return ok;
}

// ── Note extraction (mirrors C# NotesConverter::ToNotesPolyphonic) ────────────
struct BPNote { int pitch; float t0,t1,amp; };

static int hz_to_midi_idx(float hz){ return (int)std::round(12.0*std::log2(hz/440.0)+69)-BP_MIDI_OFF; }

static std::vector<BPNote> extract_notes(
    const std::vector<float>& ndata,
    const std::vector<float>& odata,
    int nf)
{
    int plo=std::max(0,hz_to_midi_idx(BP_MIN_FREQ));
    int phi=std::min(BP_N_NOTES-1,hz_to_midi_idx(BP_MAX_FREQ));

    // Infer onsets: max(onset, scaled frame_diff)
    std::vector<float> inf(odata.begin(),odata.end());
    {
        float maxo=*std::max_element(odata.begin(),odata.end());
        std::vector<float> diff(nf*BP_N_NOTES,0.f);
        for(int t=0;t<nf-1;++t)
            for(int p=0;p<BP_N_NOTES;++p)
                diff[t*BP_N_NOTES+p]=std::max(0.f,ndata[(t+1)*BP_N_NOTES+p]-ndata[t*BP_N_NOTES+p]);
        float maxd=*std::max_element(diff.begin(),diff.end());
        float sc=(maxd>1e-9f)?maxo/maxd:0.f;
        for(int i=0;i<nf*BP_N_NOTES;++i) inf[i]=std::max(inf[i],diff[i]*sc);
    }

    // Find onset local maxima
    std::vector<int> idxs;
    for(int t=1;t<nf-1;++t)
        for(int p=plo;p<=phi;++p){
            float v=inf[t*BP_N_NOTES+p];
            if(v>=BP_ONSET_THR && v>inf[(t-1)*BP_N_NOTES+p] && v>inf[(t+1)*BP_N_NOTES+p])
                idxs.push_back(t*BP_N_NOTES+p);
        }
    std::sort(idxs.begin(),idxs.end(),[&](int a,int b){ return inf[a]>inf[b]; });

    std::vector<float> rem(ndata.begin(),ndata.end());
    std::vector<BPNote> notes;

    for(int idx:idxs){
        int t0=idx/BP_N_NOTES, p=idx%BP_N_NOTES;
        if(t0>=nf-1) continue;
        int i=t0+1,k=0;
        while(i<nf-1 && k<BP_ENERGY_THR){
            if(rem[i*BP_N_NOTES+p]<BP_FRAME_THR) k++; else k=0; i++;
        }
        i-=k;
        if(i-t0<=BP_MIN_NOTE) continue;
        float amp=0;
        for(int j=t0;j<i;++j){
            int off=j*BP_N_NOTES+p;
            amp+=ndata[off]; rem[off]=0;
            if(p<BP_N_NOTES-1) rem[off+1]=0;
            if(p>0)            rem[off-1]=0;
        }
        amp/=(i-t0);
        notes.push_back({p+BP_MIDI_OFF,
                         (float)(t0*BP_FFT_HOP)/(float)BP_SR,
                         (float)(i *BP_FFT_HOP)/(float)BP_SR,
                         amp});
    }
    return notes;
}

// ── Chromagram from note events in a time window ──────────────────────────────
static void chroma_from_notes(const std::vector<BPNote>& notes,
                               float ws, float we, float* ch)
{
    for(int i=0;i<12;++i) ch[i]=0;
    float tot=0;
    for(const auto& n:notes){
        float ov=std::min(n.t1,we)-std::max(n.t0,ws);
        if(ov<=0) continue;
        float w=n.amp*ov;
        ch[n.pitch%12]+=w; tot+=w;
    }
    if(tot>0) for(int i=0;i<12;++i) ch[i]/=tot;
}

// ── Main BasicPitch chord pipeline ────────────────────────────────────────────
static int detect_basicpitch(const float* in, int nc, int sr,
                              OwnAudioMlChordResult* res, int maxr)
{
    std::vector<float> audio=resample(in,nc,sr);
    int total=(int)audio.size();
    int nof=(int)((long long)total*BP_ANNOT_FPS/BP_SR);
    if(nof<=0) return -1;

    std::vector<float> anotes(nof*BP_N_NOTES,0.f);
    std::vector<float> aonsets(nof*BP_N_NOTES,0.f);

    const int half=BP_N_OLAP_FR/2;   // 15 frames trimmed each side
    int opos=0;
    std::vector<float> wb(BP_N_SAMPLES,0.f);

    for(int cur=-BP_OLAP_LEN/2; cur<total && opos<nof; cur+=BP_HOP_SIZE){
        std::fill(wb.begin(),wb.end(),0.f);
        int as=std::max(0,cur), bo=as-cur;
        int cn=std::min(BP_N_SAMPLES-bo, total-as);
        if(cn>0) std::copy(audio.begin()+as, audio.begin()+as+cn, wb.begin()+bo);

        WinOut wo;
        if(!run_window(wb.data(),wo)) return -1;

        for(int f=half; f<BP_ANN_FRAMES-half && opos<nof; ++f,++opos)
            for(int p=0;p<BP_N_NOTES;++p){
                anotes [opos*BP_N_NOTES+p]=wo.notes [f*BP_N_NOTES+p];
                aonsets[opos*BP_N_NOTES+p]=wo.onsets[f*BP_N_NOTES+p];
            }
    }

    auto bpnotes=extract_notes(anotes,aonsets,opos);

    float tmpl[12][6][12]; build_templates(tmpl);
    float dur=(float)total/(float)BP_SR;

    std::vector<int>   wr,wt; std::vector<float> wc,wts;
    for(float t=0.f; t<dur+k_hop_sec; t+=k_hop_sec){
        float te=std::min(t+k_win_sec,dur);
        float ch[12]; chroma_from_notes(bpnotes,t,te,ch);
        int r,ti; float s; best_chord(ch,tmpl,r,ti,s);
        wr.push_back(r); wt.push_back(ti); wc.push_back(s); wts.push_back(t);
    }
    return emit_segments(wr,wt,wc,wts,res,maxr);
}

#endif  // OWNAUDIO_ML_HAS_ONNXRUNTIME

// ── Public C API ──────────────────────────────────────────────────────────────
extern "C" {

OWNAUDIO_ML_API int ownaudio_ml_detect_chords(
    const float*           input,
    int                    sample_count,
    int                    sample_rate,
    OwnAudioMlChordResult* results,
    int                    max_results,
    int*                   result_count)
{
    if(!input||sample_count<=0||!results||max_results<=0||!result_count) return -1;
    *result_count=0;

#ifdef OWNAUDIO_ML_HAS_ONNXRUNTIME
    if(g_nmp_session){
        int n=detect_basicpitch(input,sample_count,sample_rate,results,max_results);
        if(n>=0){ *result_count=n; return 0; }
        // inference failed → fall through to HPCP
    }
#endif

    *result_count=detect_hpcp(input,sample_count,sample_rate,results,max_results);
    return 0;
}

}  // extern "C"
