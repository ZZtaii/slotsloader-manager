// SF6 Mod Suite updater: what it asks of the game (update_game.hpp). Built with /EHa: a fault in a game call is caught by catch (...).
#include "update_game.hpp"

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>

#include <atomic>
#include <cstring>
#include <mutex>
#include <string>
#include <vector>

#pragma warning(push, 0)                // REFramework's header: size_t -> unsigned, an unused local
#include <reframework/API.hpp>
#pragma warning(pop)

#include "update_fs.hpp"

namespace sf6up::game {

namespace {

using API = reframework::API;
using TD = API::TypeDefinition;
using Method = API::Method;
using Obj = API::ManagedObject;

void* arg_i32(int32_t v) { return reinterpret_cast<void*>(static_cast<uintptr_t>(static_cast<uint32_t>(v))); }

TD* type(const char* name) {
    auto& a = API::get();
    return a ? a->tdb()->find_type(name) : nullptr;
}

// A method by name and number of parameters, on the type or a parent
// (the name may be a full signature, "Init(System.Int32, System.Object)": tried as it is, then by its name and arity)
Method* method_of(TD* td, const char* name, int nparams) {
    const char* open = std::strchr(name, '(');
    const size_t len = open ? (size_t)(open - name) : std::strlen(name);
    for (TD* t = td; t; t = t->get_parent_type()) {
        if (Method* m = t->find_method(name))
            if (nparams < 0 || m->get_num_params() == (uint32_t)nparams) return m;
        for (Method* m : t->get_methods()) {
            const char* n = m ? m->get_name() : nullptr;
            if (n && std::strlen(n) == len && std::strncmp(n, name, len) == 0 && (nparams < 0 || m->get_num_params() == (uint32_t)nparams)) return m;
        }
    }
    return nullptr;
}

bool is_managed(void* p) {
    auto& a = API::get();
    return p && a && a->sdk()->managed_object->is_managed_object(p);
}

// A field's address on an object (its type or a parent's)
void* field_ptr(Obj* o, const char* name) {
    if (!is_managed(o)) return nullptr;
    for (TD* t = o->get_type_definition(); t; t = t->get_parent_type())
        if (API::Field* f = t->find_field(name)) return f->is_static() ? nullptr : f->get_data_raw(o, false);
    return nullptr;
}

Obj* field_obj(Obj* o, const char* name) {
    void* p = field_ptr(o, name);
    Obj* v = p ? *reinterpret_cast<Obj**>(p) : nullptr;
    return is_managed(v) ? v : nullptr;
}

bool call(Method* m, Obj* self, std::vector<void*> args, reframework::InvokeRet* out) {
    if (!m) return false;
    *out = m->invoke(self, args);
    return !out->exception_thrown;
}

bool call_named(Obj* self, const char* name, int nparams, std::vector<void*> args, reframework::InvokeRet* out) {
    if (!is_managed(self)) return false;
    return call(method_of(self->get_type_definition(), name, nparams), self, std::move(args), out);
}

Obj* new_string(const std::string& s) {
    auto& a = API::get();
    return a ? reinterpret_cast<Obj*>(a->sdk()->functions->create_managed_string_normal(s.c_str())) : nullptr;
}

Obj* g_box = nullptr;
std::string g_labels[3];            // each button's word, still to be written on the box (empty: the game's own word)
int g_label_tries = 0;

// A button of the game's box with our word instead of the game's (Yes, No, Cancel). The box's handle holds its Param, whose
// SimpleList_3 (three buttons) and SimpleList_2 (two: seen in game 18/11, a two-button box kept "Yes" / "No" while only
// SimpleList_3 was written) have one item per button, each with a text that carries a message id; the id is emptied and the word
// written, once. False while the buttons are not built yet.
bool label_in_list(Obj* list, int idx, const std::string& word);
bool label_button(Obj* box, int idx, const std::string& word) {
    Obj* param = field_obj(box, "<Param>k__BackingField");
    if (!param) return false;
    bool any = false;
    for (const char* name : { "SimpleList_2", "SimpleList_3" }) {
        Obj* list = field_obj(param, name);
        try { if (list && label_in_list(list, idx, word)) any = true; } catch (...) {}
    }
    return any;
}
bool label_in_list(Obj* list, int idx, const std::string& word) {
    reframework::InvokeRet r{};
    if (!call_named(list, "get_Items", 0, {}, &r)) return false;
    Obj* items = static_cast<Obj*>(r.ptr);
    if (!call_named(items, "get_Count", 0, {}, &r) || (int32_t)r.dword <= idx) return false;
    if (!call_named(items, "get_Item", 1, { arg_i32(idx) }, &r)) return false;
    Obj* node = static_cast<Obj*>(r.ptr);
    if (!call_named(node, "get_Child", 0, {}, &r)) return false;
    node = static_cast<Obj*>(r.ptr);
    for (int i = 0; i < 16 && is_managed(node); ++i) {
        if (node->get_type_definition()->get_full_name() == "via.gui.Text") {
            static const uint8_t kNoId[16] = {};
            Obj* str = new_string(word);
            if (!str) return false;
            reframework::InvokeRet v{};
            if (!call_named(node, "set_MessageId", 1, { const_cast<uint8_t*>(kNoId) }, &v)) return false;
            return call_named(node, "set_Message", 1, { str }, &v);
        }
        if (!call_named(node, "get_Next", 0, {}, &r)) return false;
        node = static_cast<Obj*>(r.ptr);
    }
    return false;
}

// ---- the Details key (read in the game's code 17/11): MessageBoxMain.OnEnter builds the box's guide line (Confirm, Cancel)
// and registers it with UIWidgetCtrl_InputGuide (its id kept in the Param's GuideId). In the post of that OnEnter, for our box,
// one entry more is added there (AddData), made as Custom Combos' pause-menu keys are: Type 0, DigitalConfigId UI_Y 93 (the game
// draws T or the pad's top button), MessageId a GUID text. The key itself is read by the host (T, the pad's top button); when it is
// pressed the box is closed by its own EventCancel, and box_poll answers 3.
DetailsKey g_details;                // this box's (word empty: none)
bool g_details_hooked = false, g_details_hook_failed = false;
Obj* g_main = nullptr;               // the box's MessageBoxMain, from its OnEnter
bool g_expect = false;               // a box of ours is being opened: its OnEnter is the next one
bool g_key_was = true;               // (held when the box came: not a press)
bool g_details_hit = false;
uint8_t g_word_guid[16] = {};
std::string g_word_done;
Obj* g_entry = nullptr;              // our guide entry, rooted for the process
thread_local Obj* tl_main = nullptr;

Obj* param_of_box(Obj* box) { return box ? field_obj(box, "<Param>k__BackingField") : nullptr; }

int pre_box_enter(int, void** argv, REFrameworkTypeDefinitionHandle*, unsigned long long) {
    tl_main = nullptr;
    try {
        if (g_details.word.empty() || !g_expect) return REFRAMEWORK_HOOK_CALL_ORIGINAL;
        Obj* main = reinterpret_cast<Obj*>(argv[1]);
        reframework::InvokeRet r{};
        if (!call_named(main, "get_Param", 0, {}, &r)) return REFRAMEWORK_HOOK_CALL_ORIGINAL;
        Obj* param = static_cast<Obj*>(r.ptr);
        if (g_box && param != param_of_box(g_box)) return REFRAMEWORK_HOOK_CALL_ORIGINAL;
        tl_main = main;
    } catch (...) {}
    return REFRAMEWORK_HOOK_CALL_ORIGINAL;
}

bool add_guide_entry(Obj* main) {
    reframework::InvokeRet r{};
    if (!call_named(main, "get_Param", 0, {}, &r)) return false;
    Obj* param = static_cast<Obj*>(r.ptr);
    void* gid = field_ptr(param, "GuideId");
    if (!gid) return false;
    const int32_t guide = *reinterpret_cast<int32_t*>(gid);
    if (g_word_done != g_details.word) {
        if (!g_details.guid || !g_details.guid(g_details.word.c_str(), g_word_guid)) return false;
        g_word_done = g_details.word;
    }
    if (!g_entry) {
        TD* td = type("app.InputGuideData");
        Obj* e = nullptr;
        try { e = td ? td->create_instance(0) : nullptr; } catch (...) { e = nullptr; }
        if (!e) return false;
        e->add_ref();
        g_entry = e;
    }
    void* f_type = field_ptr(g_entry, "<Type>k__BackingField");
    void* f_cfg = field_ptr(g_entry, "<DigitalConfigId>k__BackingField");
    void* f_id = field_ptr(g_entry, "<DigitalId>k__BackingField");
    void* f_msg = field_ptr(g_entry, "<MessageId>k__BackingField");
    if (!f_type || !f_cfg || !f_id || !f_msg) return false;
    *reinterpret_cast<int32_t*>(f_type) = 0;
    *reinterpret_cast<int32_t*>(f_cfg) = 93;           // app.InputAssign.Digital.ConfigId UI_Y
    *reinterpret_cast<int32_t*>(f_id) = -1;
    std::memcpy(f_msg, g_word_guid, 16);
    Method* add = method_of(type("app.UIWidgetCtrl_InputGuide"), "AddData", 2);
    reframework::InvokeRet v{};
    return add && call(add, nullptr, { arg_i32(guide), g_entry }, &v);
}

void post_box_enter(void**, REFrameworkTypeDefinitionHandle, unsigned long long) {
    Obj* main = tl_main;
    tl_main = nullptr;
    if (!main) return;
    try {
        g_expect = false;
        main->add_ref();
        if (g_main) g_main->release();
        g_main = main;
        if (!add_guide_entry(main)) g_details.word.clear();   // no line entry: no key either
    } catch (...) { g_details.word.clear(); }
}

bool hook_details() {
    if (g_details_hooked) return true;
    if (g_details_hook_failed) return false;
    Method* m = method_of(type("app.UIFlowDialog.MessageBoxMain"), "OnEnter", 0);
    if (!m || !m->add_hook(pre_box_enter, post_box_enter, false)) { g_details_hook_failed = true; return false; }
    g_details_hooked = true;
    return true;
}

void details_reset() {
    g_details = {};
    g_expect = false;
    g_details_hit = false;
    try { if (g_main) g_main->release(); } catch (...) {}
    g_main = nullptr;
}

Obj* flow_map() {
    auto& a = API::get();
    Obj* fm = a ? a->get_managed_singleton("app.bFlowManager") : nullptr;
    Obj* work = fm ? field_obj(fm, "m_flow_work") : nullptr;
    return work ? field_obj(work, "_FlowMap") : nullptr;
}

}  // namespace

bool box_open(const std::string& text, const std::string& title, Buttons buttons, const std::string& third, const DetailsKey& details) {
    // The cursor starts on No, Back answers No (OK: the only button)
    const int no = buttons == Buttons::Ok ? 0 : 1;
    const std::string labels[3] = { {}, {}, buttons == Buttons::YesNoThird ? third : std::string() };
    return box_open_ex(text, title, buttons, labels, no, no, details);
}

bool box_open_ex(const std::string& text, const std::string& title, Buttons buttons, const std::string labels[3], int start, int cancel,
                 const DetailsKey& details) {
    try {
        if (g_box) return false;
        details_reset();
        if (!details.word.empty() && details.key && details.guid && hook_details()) {
            g_details = details;
            g_expect = true;
            g_key_was = true;
        }
        Method* m = method_of(type("app.UIFlowDialog.MessageBox"), "Start", 7);
        if (!m) return false;
        Obj* msg = new_string(text);
        Obj* ttl = new_string(title);
        if (!msg || !ttl) return false;
        // app.DialogButtonStyle: OK 1, YesNo 4, YesNoCancel 5; start: the button the cursor starts on, cancel: the one Back answers
        const int32_t style = buttons == Buttons::Ok ? 1 : buttons == Buttons::YesNoThird ? 5 : 4;
        reframework::InvokeRet r{};
        if (!call(m, nullptr, { msg, ttl, arg_i32(start), arg_i32(cancel), arg_i32(style), arg_i32(-1), arg_i32(1) }, &r)) { details_reset(); return false; }
        Obj* box = static_cast<Obj*>(r.ptr);
        if (!is_managed(box)) { details_reset(); return false; }
        box->add_ref();
        g_box = box;
        for (int i = 0; i < 3; ++i) g_labels[i] = labels ? labels[i] : std::string();
        g_label_tries = 0;
        return true;
    } catch (...) {
        return false;
    }
}

int box_poll() {
    if (!g_box) return -2;
    try {
        reframework::InvokeRet r{};
        const bool read = call_named(g_box, "get_IsEnd", 0, {}, &r);
        if (read && !(r.byte & 1)) {
            if (g_main && !g_details.word.empty() && !g_details_hit) {     // the Details key: a press, not a key held when the box came
                const bool down = g_details.key();
                if (down && !g_key_was) {
                    Method* cancel = method_of(type("app.UIFlowDialog.MessageBoxMain"), "EventCancel", 0);
                    reframework::InvokeRet c{};
                    if (cancel && call(cancel, g_main, {}, &c)) g_details_hit = true;
                }
                g_key_was = down;
            }
            if (!g_labels[0].empty() || !g_labels[1].empty() || !g_labels[2].empty()) {   // the buttons are built a frame or two after Start
                for (int i = 0; i < 3; ++i) {
                    if (g_labels[i].empty()) continue;
                    bool done = false;
                    try { done = label_button(g_box, i, g_labels[i]); } catch (...) {}
                    if (done) g_labels[i].clear();
                }
                if (++g_label_tries >= 120) for (auto& l : g_labels) l.clear();
            }
            return -1;
        }
        int answer = -2;                // unreadable: the box is lost, no answer
        if (read && g_details_hit) answer = 3;
        else if (read) {
            reframework::InvokeRet v{};
            if (call(method_of(type("app.UIFlowDialog.MessageBox"), "GetSelectValue", 1), nullptr, { g_box }, &v)) {
                const int32_t sel = (int32_t)v.dword;        // the button's place: 0 Yes (OK), 1 No, 2 the third
                answer = sel == 0 ? 1 : sel == 2 ? 2 : 0;
            }
        }
        g_box->release();
        g_box = nullptr;
        for (auto& l : g_labels) l.clear();
        details_reset();
        return answer;
    } catch (...) {
        g_box = nullptr;            // not released: the object may be gone
        for (auto& l : g_labels) l.clear();
        g_details = {};
        g_main = nullptr;
        return -2;
    }
}

void box_drop() {
    try { if (g_box) g_box->release(); } catch (...) {}
    g_box = nullptr;
    for (auto& l : g_labels) l.clear();
    details_reset();
}

bool box_is_open() { return g_box != nullptr; }

int32_t flow_id() {
    try {
        Obj* map = flow_map();
        void* p = map ? field_ptr(map, "_ID") : nullptr;
        return p ? *reinterpret_cast<int32_t*>(p) : -1;
    } catch (...) {
        return -1;
    }
}

bool in_battle() {
    try {
        Obj* map = flow_map();
        return map && field_obj(map, "m_desc") != nullptr;
    } catch (...) {
        return true;                // unreadable: not a moment to ask anything
    }
}

bool game_dialog_up() {
    try {
        auto& a = API::get();
        Obj* mgr = a ? a->get_managed_singleton("app.UIFlowManager") : nullptr;
        Obj* handles = mgr ? field_obj(mgr, "_Handles") : nullptr;
        if (!handles) return false;
        reframework::InvokeRet n{};
        if (!call_named(handles, "get_Count", 0, {}, &n)) return false;
        const int32_t count = (int32_t)n.dword;
        for (int32_t i = 0; i < count && i < 64; ++i) {
            reframework::InvokeRet it{};
            if (!call_named(handles, "get_Item", 1, { arg_i32(i) }, &it)) continue;
            Obj* h = static_cast<Obj*>(it.ptr);
            Obj* param = h ? field_obj(h, "<Param>k__BackingField") : nullptr;
            if (param && param->get_type_definition()->get_full_name() == "app.UIFlowDialog.MessageBoxParam") return true;
        }
        return false;
    } catch (...) {
        return false;
    }
}

// ---- the ticker ----------------------------------------------------------------------------------------------------------------
// The game's strip (left side of the screen), as the Recording Manager's (crsm/src/ui/ticker.cpp): a TickerRequestData handed to
// hTicker.Manager.RequestShowTicker; the game asks the request for its message through TickerRequestData.GetMessage, which the hook
// answers for our requests only (their RequestId), with the text of the moment: a strip the game draws late says where the
// download is now. The hook is set at the first strip (a player who never updates has none) and stays.
// A strip is kept on screen by ticker_keep: the game counts a request's time in its ElapsedSecond and takes the strip away at
// DisplaySecond, so the time put back to zero each frame holds it, and it leaves by itself once nobody holds it. The text on screen
// is the via.gui.Text of the strip's part (manager.UIFlowHandle -> its element -> the current state's parameter -> the
// UIPartsTicker whose Data is the request): the game reads GetMessage once when the strip comes, a later text is written there.
namespace {

constexpr uint64_t kStripKeepMs = 60000;         // a request is kept a minute (shown a few seconds), then released
constexpr size_t kStripMax = 32;

struct Strip { uint8_t id[16]; uint64_t at; Obj* req; };
std::mutex g_tk_mx;                              // the strips and the text: GetMessage is hooked on whatever thread calls it
std::vector<Strip> g_strips;
std::string g_tk_text;
std::atomic<int> g_tk_live{0};                   // the strips kept: 0 = the hook has nothing to look at
std::atomic<uint64_t> g_tk_shown{0}, g_tk_answered{0}, g_tk_rewrites{0}, g_tk_unreached{0};
std::string g_tk_on_screen;                      // game thread: the text last written on the strip kept
bool g_tk_hook_tried = false, g_tk_hooked = false;
std::string g_tk_why;                            // game thread: the step the last refused strip stopped at
std::string g_tk_notes;                          // under g_tk_mx: what the last strip's calls raised, for the log
thread_local bool t_tk_hit = false;
thread_local std::string t_tk_text;

API::Field* field_of(Obj* o, const char* name) {
    for (TD* t = o->get_type_definition(); t; t = t->get_parent_type())
        if (API::Field* f = t->find_field(name)) return f->is_static() ? nullptr : f;
    return nullptr;
}

// An integer / enum field of any width, a float or double field
void set_int(Obj* o, const char* name, int64_t v) {
    API::Field* f = field_of(o, name);
    void* p = f ? f->get_data_raw(o, false) : nullptr;
    if (!p) return;
    TD* ft = f->get_type();
    uint32_t sz = ft ? ft->get_valuetype_size() : 4;
    if (sz != 1 && sz != 2 && sz != 8) sz = 4;
    std::memcpy(p, &v, sz);
}

void set_real(Obj* o, const char* name, double v) {
    API::Field* f = field_of(o, name);
    void* p = f ? f->get_data_raw(o, false) : nullptr;
    if (!p) return;
    TD* ft = f->get_type();
    if (ft && ft->get_valuetype_size() == 8) *static_cast<double*>(p) = v;
    else *static_cast<float*>(p) = (float)v;
}

// A request's RequestId: a 16-byte value field
bool request_id(Obj* req, uint8_t out[16]) {
    if (!is_managed(req)) return false;
    API::Field* f = field_of(req, "RequestId");
    TD* ft = f ? f->get_type() : nullptr;
    if (!ft || !ft->is_valuetype() || ft->get_valuetype_size() < 16) return false;
    void* p = f->get_data_raw(req, false);
    if (!p) return false;
    std::memcpy(out, p, 16);
    return true;
}

int ticker_pre(int, void** argv, REFrameworkTypeDefinitionHandle*, unsigned long long) {
    t_tk_hit = false;
    if (g_tk_live.load(std::memory_order_relaxed) == 0) return REFRAMEWORK_HOOK_CALL_ORIGINAL;   // every ticker of the game comes here
    try {
        uint8_t id[16];
        if (!argv || !request_id(static_cast<Obj*>(argv[1]), id)) return REFRAMEWORK_HOOK_CALL_ORIGINAL;
        std::lock_guard lk(g_tk_mx);
        for (const Strip& s : g_strips) {
            if (std::memcmp(s.id, id, 16) != 0) continue;
            t_tk_text = g_tk_text;
            t_tk_hit = true;
            g_tk_answered.fetch_add(1);
            return REFRAMEWORK_HOOK_SKIP_ORIGINAL;
        }
    } catch (...) {
        t_tk_hit = false;
    }
    return REFRAMEWORK_HOOK_CALL_ORIGINAL;
}

void ticker_post(void** ret, REFrameworkTypeDefinitionHandle, unsigned long long) {
    if (!t_tk_hit) return;
    t_tk_hit = false;
    try {
        // a managed string handed to the game: one more reference for it (the game releases what GetMessage returns)
        Obj* s = ret ? new_string(t_tk_text) : nullptr;
        if (s) { s->add_ref(); *ret = s; }
    } catch (...) {
    }
}

bool ticker_hook() {
    if (g_tk_hook_tried) return g_tk_hooked;
    g_tk_hook_tried = true;
    Method* m = method_of(type("app.TickerRequestData"), "GetMessage", -1);
    g_tk_hooked = m && m->get_function_raw() && m->add_hook(ticker_pre, ticker_post, false) != 0;
    return g_tk_hooked;
}

}  // namespace

bool ticker_prepare() {
    try { return ticker_hook(); } catch (...) { return false; }
}

void ticker_text(const std::string& text) {
    std::lock_guard lk(g_tk_mx);
    g_tk_text = text;
}

bool ticker(const std::string& text, double seconds) {
    auto no = [](const char* why) { g_tk_why = why; return false; };
    try {
        ticker_text(text);
        if (!ticker_hook()) return no("no hook on TickerRequestData.GetMessage");
        TD* rtd = type("app.TickerRequestData");
        if (!rtd) return no("no type app.TickerRequestData");
        Method* get_mgr = method_of(type("app.helper.hTicker"), "get_Manager", 0);
        if (!get_mgr) return no("no hTicker.get_Manager");
        Method* init = method_of(rtd, "Init(System.Int32, System.Object)", 2);
        if (!init) return no("no TickerRequestData.Init");
        Obj* req = rtd->create_instance(1);
        if (!is_managed(req)) return no("no request made");
        req->add_ref();                              // ours, released a minute later (the game keeps the request while it shows it)
        reframework::InvokeRet r{};
        // Init's and RequestShowTicker's own answers are not asked for, as the Recording Manager's ticker never did
        std::string notes;
        if (!call(init, req, { arg_i32(112), nullptr }, &r)) notes += " Init raised;";
        set_int(req, "TickerId", 1);
        if (TD* util = type("app.TickerUtil"))       // its static constructor, as the Recording Manager's ticker runs it
            if (Method* cctor = util->find_method(".cctor")) call(cctor, nullptr, {}, &r);
        Strip s{};
        if (!request_id(req, s.id)) { req->release(); return no("the request has no RequestId"); }
        Obj* manager = call(get_mgr, nullptr, {}, &r) ? static_cast<Obj*>(r.ptr) : nullptr;
        if (!is_managed(manager)) { req->release(); return no("no ticker manager"); }
        Method* show = method_of(manager->get_type_definition(), "RequestShowTicker(app.TickerRequestData)", 1);
        if (!show) { req->release(); return no("no RequestShowTicker on the manager"); }
        s.at = GetTickCount64();
        s.req = req;
        std::vector<Obj*> old;
        {
            std::lock_guard lk(g_tk_mx);
            std::erase_if(g_strips, [&](const Strip& x) {
                const bool gone = s.at - x.at > kStripKeepMs;
                if (gone) old.push_back(x.req);
                return gone;
            });
            if (g_strips.size() >= kStripMax) { old.push_back(g_strips.front().req); g_strips.erase(g_strips.begin()); }
            g_strips.push_back(s);
            g_tk_live.store((int)g_strips.size());
        }
        for (Obj* o : old) { try { o->release(); } catch (...) {} }
        set_int(req, "Category", 6);
        set_real(req, "DisplaySecond", seconds);
        if (!call(show, manager, { req }, &r)) notes += " RequestShowTicker raised;";
        {
            std::lock_guard lk(g_tk_mx);
            g_tk_notes = notes;
        }
        g_tk_shown.fetch_add(1);
        g_tk_on_screen.clear();
        return true;
    } catch (...) {
        return no("an exception");
    }
}

Kept ticker_keep(const std::string& text) {
    try {
        Obj* req = nullptr;
        {
            std::lock_guard lk(g_tk_mx);
            if (g_strips.empty()) return Kept::Gone;
            req = g_strips.back().req;
            g_tk_text = text;                        // a strip still waiting its turn comes with it
        }
        static Method* get_mgr = method_of(type("app.helper.hTicker"), "get_Manager", 0);
        reframework::InvokeRet r{};
        Obj* manager = get_mgr && call(get_mgr, nullptr, {}, &r) ? static_cast<Obj*>(r.ptr) : nullptr;
        if (!is_managed(manager)) return Kept::Gone;
        if (field_obj(manager, "<CurrentRequest>k__BackingField") != req) {
            Obj* list = field_obj(manager, "<RequestList>k__BackingField");
            return list && call_named(list, "Contains", 1, { req }, &r) && r.byte ? Kept::Waiting : Kept::Gone;
        }
        set_real(req, "ElapsedSecond", 0.0);
        if (text == g_tk_on_screen) return Kept::Shown;
        Obj* part = manager;
        for (const char* f : { "UIFlowHandle", "<Element>k__BackingField", "_Curt", "_Param", "Ticker" }) part = part ? field_obj(part, f) : nullptr;
        Obj* label = part && field_obj(part, "Data") == req ? field_obj(part, "Text") : nullptr;
        Obj* str = label ? new_string(text) : nullptr;
        if (str && call_named(label, "set_Message", 1, { str }, &r)) {
            g_tk_on_screen = text;
            g_tk_rewrites.fetch_add(1);
        } else {
            g_tk_unreached.fetch_add(1);             // the strip's part is not there yet (it comes a few frames later), or not where it was
        }
        return Kept::Shown;
    } catch (...) {
        return Kept::Gone;
    }
}

std::string ticker_why() { return g_tk_why; }

std::string ticker_stats() {
    std::string notes;
    {
        std::lock_guard lk(g_tk_mx);
        notes = g_tk_notes;
    }
    return "strips " + std::to_string(g_tk_shown.load()) + ", texts asked by the game " + std::to_string(g_tk_answered.load()) +
           ", texts rewritten on screen " + std::to_string(g_tk_rewrites.load()) + " (not reached " + std::to_string(g_tk_unreached.load()) + ")" +
           (g_tk_hooked ? "" : g_tk_hook_tried ? ", no hook on TickerRequestData.GetMessage" : ", never used") +
           (notes.empty() ? "" : "," + notes);
}

namespace {
const char* lang_code(int32_t v) {
    switch (v) {
    case 0: return "ja"; case 1: return "en"; case 2: return "fr"; case 3: return "it"; case 4: return "de";
    case 5: return "es"; case 6: return "ru"; case 7: return "pl"; case 9: case 10: return "pt";
    case 11: return "ko"; case 12: return "zh-Hant"; case 13: return "zh-Hans"; case 21: return "ar"; case 32: return "es-419";
    default: return nullptr;
    }
}
}  // namespace

std::string language() {
    int32_t val = -1;
    try {
        TD* opt = type("app.Option");
        Method* get = opt ? method_of(opt, "GetOptionValue", 1) : nullptr;
        API::Field* list_f = opt ? opt->find_field("LanguageList") : nullptr;
        if (get && list_f) {
            reframework::InvokeRet ri{};
            void* slot = call(get, nullptr, { arg_i32(611) }, &ri) ? list_f->get_data_raw(nullptr, false) : nullptr;   // ValueType.DispLanguage
            Obj* arr = slot ? *reinterpret_cast<Obj**>(slot) : nullptr;
            const int32_t idx = (int32_t)ri.dword;
            reframework::InvokeRet len{};
            if (is_managed(arr) && idx >= 0 && call_named(arr, "get_Length", 0, {}, &len) && idx < (int32_t)len.dword) {
                reframework::InvokeRet v{};
                if (call_named(arr, "get_Item", 1, { arg_i32(idx) }, &v)) val = (int32_t)v.dword;
            }
        }
        if (val < 0) {
            reframework::InvokeRet r{};
            if (call(method_of(type("via.SystemService"), "get_Language", 0), nullptr, {}, &r)) val = (int32_t)r.dword;
        }
    } catch (...) {
    }
    const char* c = lang_code(val);
    return c ? c : "en";
}

std::string package_version(const KnownPackage& k, const std::wstring& root) {
    const std::string proof = k.proof;
    const std::wstring base = widen(proof.substr(proof.find_last_of('/') + 1));
    HMODULE h = GetModuleHandleW(base.c_str());
    if (!h) return {};
    // the module running is the game folder's file, not another copy of the same name
    std::wstring path(32768, L'\0');
    path.resize(GetModuleFileNameW(h, path.data(), (DWORD)path.size()));
    const std::wstring want = join(root, proof);
    if (CompareStringOrdinal(path.c_str(), (int)path.size(), want.c_str(), (int)want.size(), TRUE) != CSTR_EQUAL) return {};
    const char* v = nullptr;
    try {
        using VersionFn = const char* (*)();
        if (auto fn = reinterpret_cast<VersionFn>(GetProcAddress(h, "sf6suite_package_version"))) v = fn();
        if (!v && std::strcmp(k.id, "native_core") == 0) {
            using GetApi = const void* (__cdecl*)(uint32_t);
            if (auto get = reinterpret_cast<GetApi>(GetProcAddress(h, "sf6core_get_api"))) {
                // SF6CoreAPI: abi_version, struct_size, then get_version (sf6core_api.h, append-only)
                struct Head { uint32_t abi, size; const char* (__cdecl* get_version)(void); };
                if (const Head* api = static_cast<const Head*>(get(1)); api && api->size >= sizeof(Head) && api->get_version) v = api->get_version();
            }
        }
    } catch (...) {
        v = nullptr;
    }
    if (!v) return {};
    std::string s(v, strnlen(v, 32));
    return parse_version(s) ? s : std::string();
}

}  // namespace sf6up::game
