using System;
using System.Collections.Generic;

namespace SlotsInstaller
{
    // Built-in game facts captured on 2026-10-13 from SlotsLoader 1.9.6's
    // vanilla_costume_index.tsv (game version of October 13, 2026). Used when
    // the loader's index is not installed. A newer TSV in the game folder wins.
    internal static class GameTables
    {
        internal static readonly Dictionary<string, string> Suffixes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { {"chain","52"}, {"fchar","18"}, {"ikbodyrig","3"}, {"jcns","22"}, {"jmap","19"}, {"mcambank","3"}, {"mdf2","31"}, {"mesh","230110883"}, {"motbank","3"}, {"msg","21"}, {"pfb","17"}, {"scn","20"}, {"tex","241101895"}, {"user","2"} };
        // "esfNNN/FFF=costume number" (0 = Outfit 1). A folder used by several costumes maps to the lowest.
        internal const string CostumeFolders = "esf001/001=0 esf001/002=1 esf001/003=2 esf001/005=4 esf002/001=0 esf002/002=1 esf002/003=2 esf002/004=3 esf002/005=4 esf003/001=0 esf003/002=1 esf003/003=2 esf003/004=3 esf003/005=4 esf004/001=0 esf004/002=1 esf004/003=2 esf004/004=3 esf004/005=4 esf005/001=0 esf005/002=1 esf005/003=2 esf005/004=3 esf005/005=4 esf006/001=0 esf006/002=1 esf006/003=2 esf006/004=3 esf006/005=4 esf007/001=0 esf007/002=1 esf007/003=2 esf007/005=4 esf008/001=0 esf008/002=1 esf008/003=2 esf009/001=0 esf009/002=1 esf009/003=2 esf009/004=3 esf009/005=4 esf010/001=0 esf010/002=1 esf010/003=2 esf010/005=4 esf011/001=0 esf011/002=1 esf011/003=2 esf011/004=3 esf011/005=4 esf012/001=0 esf012/002=1 esf012/003=2 esf012/005=4 esf013/001=0 esf013/002=1 esf013/003=2 esf013/004=3 esf013/005=4 esf014/001=0 esf014/002=1 esf014/003=2 esf014/005=4 esf015/001=0 esf015/002=1 esf015/003=2 esf015/005=4 esf016/001=0 esf016/002=1 esf016/003=2 esf016/004=3 esf016/005=4 esf017/001=0 esf017/002=1 esf017/003=2 esf017/005=4 esf018/001=0 esf018/002=1 esf018/003=2 esf018/005=4 esf019/001=0 esf019/002=1 esf019/003=2 esf019/005=4 esf020/001=0 esf020/002=1 esf020/003=2 esf020/005=4 esf021/001=0 esf021/002=1 esf021/003=2 esf021/004=3 esf021/005=4 esf022/001=0 esf022/002=1 esf022/003=2 esf022/005=4 esf025/001=0 esf025/002=1 esf025/003=2 esf025/005=4 esf026/001=0 esf026/002=1 esf026/003=2 esf026/005=4 esf027/001=0 esf027/002=1 esf027/003=2 esf027/005=4 esf028/001=0 esf028/002=1 esf028/003=2 esf028/005=4 esf029/001=0 esf029/002=1 esf029/003=2 esf029/004=3 esf029/005=4 esf030/001=0 esf030/002=1 esf030/003=2 esf030/004=3 esf030/005=4 esf031/001=0 esf031/002=1 esf031/003=2 esf031/005=4 esf032/001=0 esf032/002=1 esf032/003=2 esf032/005=4 esf033/001=0 esf033/002=1 esf033/005=4 esf034/001=0 esf034/002=1";
    }
}
