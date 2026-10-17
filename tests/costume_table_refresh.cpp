#include "patch.hpp"
#include <algorithm>
#include <array>
#include <cstdio>
#include <cstring>
#include <fstream>
#include <iterator>
#include <stdexcept>

using Bytes = std::vector<uint8_t>;
constexpr uint32_t ROOT = 0x2f581bd0, REC = 0x3bbf93ac, MSG = 0x9b851e9b;
size_t aligned(size_t n, size_t a) { return (n + a - 1) / a * a; }
template<class T> T get(const Bytes& b, size_t p) { T v; std::memcpy(&v, b.data() + p, sizeof v); return v; }
template<class T> void set(Bytes& b, size_t p, T v) { std::memcpy(b.data() + p, &v, sizeof v); }
template<class T> void add(Bytes& b, T v) { size_t p=b.size(); b.resize(p+sizeof v); set<T>(b,p,v); }
void pad(Bytes& b, size_t n) { b.resize(aligned(b.size(), n)); }
struct Record { uint32_t id, fighter, costume, sort=1, visual=0, arrange=0; };
struct Instance { uint32_t type=0, crc=0; Bytes data; };

// Independent fixture writer: vanilla message/record pairs, root, then shared slot messages/records.
Bytes fixture(const std::vector<Record>& vanilla, const std::vector<Record>& slots={}) {
    std::vector<Instance> ins(1);
    std::vector<uint32_t> refs;
    auto msg = [&](uint32_t id) {
        Instance m{MSG, 0x74c329fa, {}}; add<uint32_t>(m.data,id);
        // Store only the GUID here; final writer applies position-dependent alignment.
        for (uint32_t n=0;n<4;++n) add<uint32_t>(m.data, id + 0x10000000u*n);
        ins.push_back(m); return uint32_t(ins.size()-1);
    };
    auto rec = [&](const Record& r, uint32_t message) {
        Instance i{REC, 0x88653730, {}};
        for (uint32_t v : {r.id,r.id,r.fighter,0u,r.costume,message,1u,r.sort,r.visual,r.arrange}) add<uint32_t>(i.data,v);
        refs.push_back(uint32_t(ins.size())); ins.push_back(i);
    };
    for (auto& r : vanilla) { uint32_t m=msg(r.costume+1); rec(r,m); }
    uint32_t root=uint32_t(ins.size()); ins.push_back({ROOT,0x71c6b1b3,{}});
    std::vector<uint32_t> messages;
    for (size_t i=0;i<slots.size();++i) messages.push_back(msg(5000+uint32_t(i)));
    for (size_t i=0;i<slots.size();++i) rec(slots[i],messages[i]);
    add<uint32_t>(ins[root].data,uint32_t(refs.size()));
    for (uint32_t x:refs) add<uint32_t>(ins[root].data,x);
    size_t data_off=aligned(52+8*ins.size(),16);
    Bytes out(48+data_off); std::memcpy(out.data(),"USR\0",4);
    set<uint64_t>(out,16,48); set<uint64_t>(out,24,48); set<uint64_t>(out,32,48);
    std::memcpy(out.data()+48,"RSZ\0",4); set<uint32_t>(out,52,16);
    set<uint32_t>(out,56,1); set<uint32_t>(out,60,uint32_t(ins.size()));
    set<uint64_t>(out,72,52); set<uint64_t>(out,80,data_off); set<uint64_t>(out,88,data_off);
    set<uint32_t>(out,96,root);
    Bytes data;
    for(size_t i=1;i<ins.size();++i) {
        set<uint32_t>(out,100+i*8,ins[i].type); set<uint32_t>(out,104+i*8,ins[i].crc);
        pad(data,4);
        if(ins[i].type==MSG) {
            add<uint32_t>(data,get<uint32_t>(ins[i].data,0)); pad(data,8);
            data.insert(data.end(),ins[i].data.begin()+4,ins[i].data.end());
        } else data.insert(data.end(),ins[i].data.begin(),ins[i].data.end());
    }
    out.insert(out.end(),data.begin(),data.end()); return out;
}

struct View {
    size_t d; uint32_t root, count;
    std::vector<size_t> starts;
    explicit View(const Bytes& b) : d(48+get<uint64_t>(b,80)), root(get<uint32_t>(b,96)), count(get<uint32_t>(b,60)), starts(count) {
        size_t p=0;
        for(uint32_t i=1;i<count;++i) {
            p=aligned(p,4); starts[i]=d+p;
            uint32_t t=get<uint32_t>(b,100+i*8);
            if(t==MSG) p=aligned(p+4,8)+16;
            else if(t==REC) p+=40;
            else p+=4+4ull*get<uint32_t>(b,d+p);
        }
    }
};
Bytes read(const char* name) {
    std::ifstream f(name,std::ios::binary); if(!f) throw std::runtime_error("cannot open input");
    return Bytes(std::istreambuf_iterator<char>(f),{});
}
int passed=0;
void check(bool ok,const char* name) {
    if(!ok) throw std::runtime_error(name);
    ++passed; std::printf("PASS %s\n",name);
}

int main(int argc,char** argv) try {
    if(argc==5 && std::strcmp(argv[1],"--refresh")==0) {
        Bytes old=read(argv[2]), base=read(argv[3]); std::string reason;
        if(!refresh_costume_table(old,base,&reason)) { std::fprintf(stderr,"REFUSED: %s\n",reason.c_str()); return 1; }
        std::ofstream f(argv[4],std::ios::binary); f.write(reinterpret_cast<const char*>(old.data()),old.size());
        if(!f) throw std::runtime_error("cannot write output");
        View v(old); std::printf("Refreshed %zu bytes; vanilla root %u, %u total records\n",old.size(),v.root,get<uint32_t>(old,v.starts[v.root]));
        return 0;
    }
    if(argc!=1) { std::fprintf(stderr,"usage: costume_table_refresh [--refresh static current output]\n"); return 2; }
    std::vector<Record> previous{{1,2,0},{2,3,0}}; // Repeated vanilla message IDs/GUIDs are legitimate.
    std::vector<Record> current{{1,2,0,91,4,7},{2,3,0},{137,34,0},{143,34,1},{147,30,3}};
    std::vector<Record> slots{{5000,2,5,101,5,0},{5001,3,5,102,5,0}};
    Bytes shipped=fixture(previous,slots), vanilla=fixture(current), refreshed=shipped;
    std::string reason;
    check(refresh_costume_table(refreshed,vanilla,&reason),"refresh new official costumes and changed vanilla metadata");
    check(refreshed==fixture(current,slots),"preserve complete current table and remap generated message references");
    Bytes repeated=refreshed;
    check(refresh_costume_table(repeated,vanilla,&reason)&&repeated==refreshed,"idempotent rebase");
    View rv(refreshed), vv(vanilla);
    check(std::equal(vanilla.begin()+vv.d,vanilla.begin()+vv.starts[vv.root],refreshed.begin()+rv.d),"vanilla instance bytes and indices unchanged");
    size_t kept=0,removed=0;
    check(trim_costume_table(refreshed,{5001},&kept,&removed)&&kept==1&&removed==1,"trim only unwanted generated record");
    View tv(refreshed);
    check(get<uint32_t>(refreshed,tv.starts[tv.root])==current.size()+1,"trim preserves every current vanilla record");
    check(std::equal(vanilla.begin()+vv.d,vanilla.begin()+vv.starts[vv.root],refreshed.begin()+tv.d),"trim leaves current vanilla metadata unchanged");
    auto reject = [&](Bytes old,Bytes base,const char* label) {
        Bytes unchanged=old; std::string why;
        check(!refresh_costume_table(old,base,&why)&&old==unchanged&&!why.empty(),label);
    };
    Bytes bad=vanilla; set<uint64_t>(bad,80,UINT64_MAX); reject(shipped,bad,"reject invalid data offset without mutation");
    bad=vanilla; set<uint32_t>(bad,60,UINT32_MAX); reject(shipped,bad,"reject oversized instance count");
    bad=vanilla; set<uint32_t>(bad,vv.starts[vv.root],UINT32_MAX); reject(shipped,bad,"reject oversized root array");
    bad=vanilla; set<uint32_t>(bad,vv.starts[vv.root]+4,UINT32_MAX); reject(shipped,bad,"reject dangling root record");
    bad=vanilla; set<uint32_t>(bad,vv.starts[2]+20,UINT32_MAX); reject(shipped,bad,"reject dangling record message");
    bad=vanilla; set<uint32_t>(bad,100+2*8,0xdeadbeef); reject(shipped,bad,"reject unknown schema");
    bad=vanilla; for(uint32_t i=1;i<vv.count;++i) if(get<uint32_t>(bad,100+i*8)==REC) set<uint32_t>(bad,104+i*8,0x12345678);
    reject(shipped,bad,"reject mismatched type signatures");
    auto colliding=current; colliding[0].id=5000; reject(shipped,fixture(colliding),"reject official and generated record id collision");
    colliding=current; colliding[0].fighter=2; colliding[0].costume=5; reject(shipped,fixture(colliding),"reject occupied official costume slot");
    bad=vanilla; set<uint32_t>(bad,vv.starts[1],5000); reject(shipped,bad,"reject message id collision");
    bad=vanilla; View sv(shipped); size_t old_guid=sv.d+aligned(sv.starts[sv.root+1]-sv.d+4,8);
    size_t base_guid=vv.d+aligned(vv.starts[1]-vv.d+4,8);
    std::copy(shipped.begin()+old_guid,shipped.begin()+old_guid+16,bad.begin()+base_guid);
    reject(shipped,bad,"reject message GUID collision independently");
    bad=vanilla; set<uint32_t>(bad,vv.starts[vv.root]+8,get<uint32_t>(bad,vv.starts[vv.root]+4)); reject(shipped,bad,"reject duplicate root record");
    // Fighter 34 (Arjun) reserved records next to its official costumes 0/1 and C. Viper's costume 3
    std::vector<Record> arjun{{5000,2,5,101,5,0},{8100,34,5,3201,5,0},{8199,34,104,3300,104,0},{7702,30,7,2802,7,0}};
    Bytes with_arjun=fixture(previous,arjun), arjun_refreshed=with_arjun;
    check(refresh_costume_table(arjun_refreshed,vanilla,&reason)&&arjun_refreshed==fixture(current,arjun),
          "rebase fighter 34 reserved records beside official records 137/143/147");
    size_t arjun_kept=0,arjun_removed=0;
    check(trim_costume_table(arjun_refreshed,{8100},&arjun_kept,&arjun_removed)&&arjun_kept==1&&arjun_removed==3
          &&get<uint32_t>(arjun_refreshed,View(arjun_refreshed).starts[View(arjun_refreshed).root])==current.size()+1,
          "trim keeps one fighter 34 slot and every official record");
    reject(fixture(previous,{{8100,34,1,3201,1,0}}),vanilla,"reject reserved record on official fighter 34 costume 1");
    reject(fixture(previous,{{7700,30,3,2801,3,0}}),vanilla,"reject reserved record on official C. Viper costume 3");
    reject(fixture(previous,{{143,34,5,3201,5,0}}),vanilla,"reject reserved record id equal to official record 143");
    bool truncations=true;
    for(size_t n=0;n<shipped.size();++n) {
        Bytes cut(shipped.begin(),shipped.begin()+n), before=cut;
        if(refresh_costume_table(cut,vanilla,&reason)||cut!=before) {truncations=false;break;}
    }
    check(truncations,"reject every truncated shipped-table length without mutation");
    truncations=true;
    for(size_t n=0;n<vanilla.size();++n) {
        Bytes cut(vanilla.begin(),vanilla.begin()+n), copy=shipped;
        if(refresh_costume_table(copy,cut,&reason)||copy!=shipped) {truncations=false;break;}
    }
    check(truncations,"reject every truncated current-table length without mutation");
    // Slot records added to the game's own table (game-derived structural files)
    std::vector<Record> game{{1,1,0},{2,2,0},{137,34,0}};
    std::vector<Record> added{{5000,1,5,101,5,0},{5001,1,6,102,6,0},{8100,34,5,3201,5,0}};
    std::vector<CostumeSlotRecord> add_list{{5000,1,5,101,0},{5001,1,6,102,1},{8100,34,5,3201,2}};
    std::vector<Bytes> guids;
    for (uint32_t id : {5000u,5001u,5002u}) { Bytes g; for (uint32_t n=0;n<4;++n) add<uint32_t>(g,id+0x10000000u*n); guids.push_back(g); }
    Bytes game_table=fixture(game), with_slots=game_table;
    check(add_costume_records(with_slots,add_list,5000,guids,&reason)&&with_slots==fixture(game,added),
          "add slot records to the game's table: same bytes as the independent writer");
    Bytes rebased=with_slots;
    check(refresh_costume_table(rebased,game_table,&reason)&&rebased==with_slots,
          "refresh of a table built from the current game is the identity");
    std::vector<std::pair<int,int>> listed;
    check(costume_table_records(game_table,listed)&&listed==std::vector<std::pair<int,int>>{{1,0},{2,0},{34,0}}
          &&!costume_table_records(with_slots,listed),
          "list the game's records; refuse a table that already has slot records");
    auto refuse_add = [&](Bytes t,const char* label) {
        Bytes unchanged=t; std::string why;
        check(!add_costume_records(t,add_list,5000,guids,&why)&&t==unchanged&&!why.empty(),label);
    };
    refuse_add(fixture({{1,2,0},{2,3,0}}),"refuse a table without a fighter 1 / Outfit 1 record to copy, untouched");
    refuse_add(with_slots,"refuse a table with instances after its root, untouched");
    refuse_add(Bytes(game_table.begin(),game_table.end()-1),"refuse a truncated table, untouched");
    check(costume_slot_index(1)==0&&costume_slot_index(22)==21&&costume_slot_index(25)==22&&costume_slot_index(34)==31
          &&costume_slot_index(35)==32&&costume_slot_index(23)>=1000,
          "slot blocks by fighter id: 1-22 -> 0-21, 25+ -> 22+ (Arjun 31, a fighter 35 gets 32)");
    Bytes g; const uint8_t want[16]={0xd4,0xc3,0xb2,0xa1,0x01,0x00,0x00,0x40,0x80,0x00,0x00,0x00,0x00,0x00,0x13,0x88};
    check(guid_bytes("a1b2c3d4-0001-4000-8000-000000001388",g)&&g==Bytes(want,want+16)&&!guid_bytes("a1b2",g),
          "System.Guid bytes of the slot message GUIDs");
    std::printf("%d checks passed\n",passed); return 0;
} catch(const std::exception& e) {std::fprintf(stderr,"FAIL: %s\n",e.what());return 1;}
