namespace Client.Compilation.Native;

/// <summary>Finite P16 declaration. Root must review the complete native/data
/// closure before enabling readiness; this never selects a bundled default.</summary>
public static class NativeCompilerInputBudgetPins
{
    public static bool Ready => true;
    public const int ResourceCount = 4557, TotalBytes = 262320783;
    public const string ClosureHash = "8e3fcc0c3255613609a423a5440934b261feee32a5cc73510f4633648b4a135e";
    public const int DviConverterLoaderLength = 13624, DviConverterWasmLength = 697401;
    public const string DviConverterLoaderHash = "75d1c4441867f3875f82d09409c3eb0ebb8ee0d31d73d8008fc150605c023b09";
    public const string DviConverterWasmHash = "44fe2d05227f7e8d0736ab929825088619e1f741afcb4b788180a6d4c743fe9c";
    public const int XeConverterLoaderLength = 13380, XeConverterWasmLength = 696113;
    public const string XeConverterLoaderHash = "a61b06d8530fc0e2a09cc874c8e36260925cc15308b674e946634689d2d62bbc";
    public const string XeConverterWasmHash = "44ae512b65187816e6b43e40c1d94857734336721f30090ea2ed83f111001e12";
    public const int XeCatalogLength = 12729, ParentXeCatalogLength = 12315;
    public const string XeCatalogHash = "7e714695c3bf62543445856f69d6690de0b4b5cecec8def6624b64875522768b";
    public const string ParentXeCatalogHash = "380ef7057355bff9024f448cf132e6c563364071ee36251d557d95c4652d1e65";
    // Exact source-associated additions, including the four real XITS faces.
    // No alias or replacement of an existing font is permitted.
    public static IReadOnlyList<NativeResourcePin> AdditionalResources { get; } = Array.AsReadOnly(new NativeResourcePin[]
    {
        new("Udsrom.fd", 211, "24e14828e82b7eea9aad9cc1fce2071848847dcb6cd9bf12fe0ad39aedb7dff6"),
        new("Udsss.fd", 210, "ed356034b355598ed26e7b64f0166a97383715d49ed2213df44ae07a329c16b1"),
        new("Ustmry.fd", 1766, "a6e7ccc1c8ff376dbbf0a3c1418f8ded711898ddcfc46a1a7156c0bf66e14bbe"),
        new("XITS-Bold.otf", 154292, "6076d16614af00f81163b6a5886811bbb2db6dee8ee81827212c8029042693de"),
        new("XITS-BoldItalic.otf", 112820, "fde9c2628b3cd960fc3b068d4ee491801b64b54f4cdb31921e8b380ccea36018"),
        new("XITS-Italic.otf", 116744, "c917a8c25a1ee196fff88e6e95fa82f1a18a758563b04dd68bc13acbbe98dc17"),
        new("XITS-Regular.otf", 270632, "9cd5e6b9bac7647cc714c17d7c9e2e5087f8551cf1bec48a8e753172dbc641b8"),
        new("algc.sty", 1280, "068837b255bd0b905458ee1576f32e0b38b651e2e3b52ff84ee35b00c0136e00"),
        new("algcompatible.sty", 2994, "e4ce6965c55d5b50e1eb3cdfe0f649a2e4c15ca1e6e52508c5381400c9b30d8b"),
        new("algmatlab.sty", 2217, "76c043dacc122f2bbab8f17043e9522c743d29c975cee17daccdf1ec8aa0a26d"),
        new("algorithm.sty", 3249, "d63fdf24879f0efd535bc6d06bc2a008b79ae77f6d6d2bc96156ff1f35e96bd7"),
        new("algorithmic.sty", 9318, "7fe47ed7f8222c56452bd73bf0682e72023ece06d59207653a02e080a341118d"),
        new("algorithmicx.sty", 26750, "65d5bc723633cf909cd8586b85c9dd0bc2714f74c0a1a569552aca4bfbdf2acd"),
        new("algpascal.sty", 1323, "e72157051c0afc391865e2daf3f4f2c5eb9117c371b5cdb58ef492f1368b18a9"),
        new("algpseudocode.sty", 3457, "d593ce32962f09494d212c176af0e739e51d89d65032c5a0c1b82c543badbccb"),
        new("american-gost.lbx", 1133, "db52087e860e65686f900383756de2c7c4ebe8766b5e5a9d38385eb514ae5fa4"),
        new("bbm.sty", 1644, "6d54416a5c8291c6a4810a2324b2a58658faef774748b395524a3a50593f4c97"),
        new("biblatex-gost.dbx", 2582, "73fd5fda99ba939a08d29fbc94f2c344efb94f201a6207c3056627e9de4ef476"),
        new("biblatex-gost.def", 12727, "b4e1025567a481940938a06d9e4ce19a590e2d523811fe86b2cec14026681ce3"),
        new("brazilian-gost.lbx", 346, "92ea7268d83722d4219f64b54866e8548572cf5633dba2f969057fd898a1f0d5"),
        new("british-gost.lbx", 1100, "83a0a01e80197d477808ff38e9c6ebf774eb1874623f77e9b282ddded1019bca"),
        new("catalan-gost.lbx", 336, "0359582d91d47f0e3874f63e66d2906f4b3b8a70369d83ec7e9305a25adb9ade"),
        new("changepage.sty", 4523, "c26adc410847658bbb8ddcadfb5fee6fa71254f07df5f007d2126760d6715a02"),
        new("chngpage.sty", 4325, "aa3f87b58bc1ac695d29f397a988b796d2596a0c5352793a1966a02a2cc60670"),
        new("croatian-gost.lbx", 338, "185a0ffd14c05b02c880845995ede6e02beccee9be4f8a89f5f1f2851634151d"),
        new("dsfont.sty", 230, "d52d0b44c12cada19fb32d97dc960c6d0769f5687ae554225da7b2c288d80214"),
        new("english-gost.lbx", 1098, "a415df096d51bc313110ac0eeda852944ac16f5c21cd74bf29c9e558fe364b1c"),
        new("french-gost.lbx", 957, "d8e5364ea32de1ad7036d71ca72fa0f8a6e7c87a26b53e2b0426aeada44c93ab"),
        new("galician-gost.lbx", 293, "3e1657d5615414f0c774ef9dc65e51fac76802c45b2f078a370f4857e6c1b9a8"),
        new("german-gost.lbx", 934, "cadbcce4bcd779a555ec4e8a07095c69234149ff708a203cc59fd00360df1167"),
        new("gost-alphabetic-min.bbx", 277, "a0cf32c077dd9c3da7ded41955f8654b828012f9e52e36e310e1eeea0dc9f244"),
        new("gost-alphabetic-min.cbx", 232, "51c8945da4646154ebea7ebf3ccf29db98242d944f0ebd7f23f1c4b80917934d"),
        new("gost-alphabetic-min.dbx", 174, "094d739585ba4dc42c33ea58eb554598585a712ee324ab385fe53e10a6b51fbb"),
        new("gost-alphabetic.bbx", 1226, "997258d90979052ecb65aa18b61d101d535412aeb31a76d616f5a6d7b25a5da9"),
        new("gost-alphabetic.cbx", 189, "a3944cb7c550b646346f44937ed16d4511a3f0637d5cf260e616bf291a7882c8"),
        new("gost-alphabetic.dbx", 170, "ed022c523dd81e1be4c0ad1460294832274908fac19a9255aebf34ab848e3dce"),
        new("gost-authoryear-min.bbx", 277, "66a7da5bbf1e14004e5b56ec97739bfe41e63f009dd59b176ea4d369a00327a7"),
        new("gost-authoryear-min.cbx", 232, "f4e90f6de208ce480f619a0021453ceb1ca92c703607534ffd3c17b360362c21"),
        new("gost-authoryear-min.dbx", 174, "54e3b629a66805cda8d1c8a3e35044f8ebce74438f4de98ed84ac4a05ba45d10"),
        new("gost-authoryear.bbx", 11758, "34c90db89596ad2325271e915813db0be65bead4b3ed05c0aa3c85738af9642f"),
        new("gost-authoryear.cbx", 11512, "25ccc6bf04fa1fbac2d4e577a2c32f730031dcc4611453ecea20b2460bfb13e9"),
        new("gost-authoryear.dbx", 170, "da7c20b2fc150f9e693a48a751ae3cf742a40f068532d0a347555278f705000c"),
        new("gost-footnote-min.bbx", 144, "148fba89e0afadeac0f25a204be5d434cd6a62a52fb3494c2b0ca15c3e7ee242"),
        new("gost-footnote-min.cbx", 290, "2e588642aeb1a44108b08edeb117578b7e798dbd4c2b9b80fcca970f2832c1ed"),
        new("gost-footnote-min.dbx", 172, "6c772554f7c3f8651a0468331bc4cc49b3ea757b5deb5fe81e96fea6603f3dff"),
        new("gost-footnote.bbx", 136, "9e7e82bfade640a2e402f8625cab6e2be8090680fbfbf63b6a36d4b3b70056a0"),
        new("gost-footnote.cbx", 226, "55b81ac6bb9b669bc328126d501d1ee99275a70ee5e6f39ff08a2a0ad76cb9e0"),
        new("gost-footnote.dbx", 168, "4ab960e714a54a87dbf655fe893820b0ff2a1320a327223182c2907965ce31da"),
        new("gost-inline-min.bbx", 269, "c005f2c365f92306d37e6dec5a74aafa9262a7ec150b3cdf3bbf73ad42fb2dff"),
        new("gost-inline-min.cbx", 286, "bb9923c82e1d40372248fac2b5d7a310decabc155ca08fad15a6bdc2af002012"),
        new("gost-inline-min.dbx", 170, "9ee49cf34b5400ba7040282174ff0fae2051722a6e5fa768ff2d5a043a6c3140"),
        new("gost-inline.bbx", 1327, "6d0350950130877645fcad05b4ff6c857493ffe83d2dca3999b7dbe837afe5c9"),
        new("gost-inline.cbx", 17106, "01e7a417050c20ef1ff6f37329d06d16d14e95f1501485ec139f115b202d2029"),
        new("gost-inline.dbx", 166, "5b7c86827c2ff7b722f5e93542a5ce6758250e61b84326253e8ba13eae111826"),
        new("gost-numeric-min.bbx", 271, "49f1ce269463ae7ef7aae30eddf37c0d69083a5d2776cfc5227bcf73768faa9e"),
        new("gost-numeric-min.cbx", 226, "90e44003dd80728cf6237013504fe8675f488b3a338033babc58a0409b0e49da"),
        new("gost-numeric-min.dbx", 171, "34042a00dfac89dc944d0a72b2b42015f366e8237920dd020482bb3126a261b6"),
        new("gost-numeric.bbx", 1607, "440398a12dce997d378678749d142ab37b970718d0a6c88dcfa5543121ecbf21"),
        new("gost-numeric.cbx", 263, "9be94ad253bf00cf07682cdcc70f306643b57ebe818548df3f4c4b64757788e4"),
        new("gost-numeric.dbx", 167, "ee302299b2a1598f84a7321011e45ea4be54e1f2b4a46cea9448d8d163027a8f"),
        new("gost-standard.bbx", 75813, "32d06b38e1e111e381b34f77c9eee5af49b75cc99a61164925a0f0f29a3e57ed"),
        new("greek-gost.lbx", 289, "57d2b96c43e3b4a666ed23c5dbef4cd8fd87061e09f61aaafbe18087bfbcf8f3"),
        new("icelandic-gost.lbx", 368, "1273d490aa828384d51d798a4ab7f85779a7f2cb790f64790a5c07ad0ee4a3b1"),
        new("import.sty", 6201, "ddcc378af8f3710502e60a97d0f6d65cdb3f3252528332edd58d564410599cd2"),
        new("italian-gost.lbx", 286, "a48a02c891f9d7242645f18531bd52c13f9ab85e2a02d640bad98227ea3eabbc"),
        new("notices/original-thesis-packages/xetex/README", 708, "451765fc0efe726fca118326bf839a157ccd196a77e476da7b1d80ceaa341dce"),
        new("notices/thesis-packages/algorithmicx/README", 256, "815ac494ff7034f137dedb250f8ce64df15bc89eb027d7e28e56694d0d62c547"),
        new("notices/thesis-packages/algorithms/COPYING", 26527, "a9bdde5616ecdd1e980b44f360600ee8783b1f99b8cc83a2beb163a0a390e861"),
        new("notices/thesis-packages/algorithms/README", 768, "0376218331d458f7867c6ac73760216af20a2865cde59023518ee5bd26a44cc6"),
        new("notices/thesis-packages/bbm-macros/README", 947, "c69c57b3434c92d4ef6d33449d6ae72dcde86263f2642e576d9ea4ce21f4bcaf"),
        new("notices/thesis-packages/bbm/README", 2480, "7b085ddd6a7f340f66f580b4eddb3905aee9ecb005b0a6c5986f6f5072f588a1"),
        new("notices/thesis-packages/biblatex-gost/README.md", 2001, "44b2ce533ac29783f13fbbb5910939084303fbc8915188f8eabfac8f5b90dc31"),
        new("notices/thesis-packages/changepage/README", 2818, "d9cdbab34d2dbeb9bd7dfb94740bd6e9e3a18b6682c95780a1dda7352085cdba"),
        new("notices/thesis-packages/doublestroke/README", 4762, "ccb7766b328e115f59a4bfb9954336190c1f2bf81c364d79379af384a6be7333"),
        new("notices/thesis-packages/import/README", 647, "55e8d66ae290faa851fea5a69d5e5018ec64811b480fb562e374be775542e382"),
        new("notices/thesis-packages/stmaryrd/README", 359, "1fe762e776c95c09b1b3a2a8221cab4388a998d791d08a1785d925c9b3a2698e"),
        new("notices/thesis-packages/stmaryrd/README.hoekwater", 1140, "8abeab633dbdca0b507c42996674a36e371a39784300a52b18adb44ee6254a08"),
        new("notices/thesis-packages/totcount/README", 1613, "f45b8ca80822d256e84e00ddb8884d5f5133ebf4fc2653d678be04c13cc2ff3f"),
        new("notices/thesis-packages/xits/OFL.txt", 4998, "10c83acb7bf240c6e263906e0905c8e419f3483c89141e83243465c221d322ad"),
        new("notices/thesis-packages/xits/README.txt", 652, "67432af9a4ed7fdf43126fc881105a06471d1f308c0e5bb0c1e2136e431775e5"),
        new("portuguese-gost.lbx", 346, "02798d271f3a829dd4575b34664c242863c3a04504e9f1a00d70576abf5e4fe3"),
        new("russian-gost.lbx", 20700, "e4749b72cbf255b7c2f26c9e9fc2aa492307642a3ecde5da560a8d0f810f4fc7"),
        new("slovene-gost.lbx", 330, "e68ae42360f044bd00a57ddd8eab70bb81bed6fc338c5be142dbde71ca4e204d"),
        new("spanish-gost.lbx", 336, "4227cd07c98a9a69624b4653444a2c68c41f1237c72bcf49d011ed96811a409a"),
        new("stmary10.pfb", 27430, "eff077f7d9ca7c2f02695e1c51bb97327f097dbdf6b2677579772bcd4f6a6844"),
        new("stmary10.tfm", 848, "9c41cd0d9cfb12c5f7ed6dccfe0367be6378e5b755a5b22424c47b975b57af8f"),
        new("stmary5.pfb", 28325, "f1ed664a815eddf1da7fe4fc037361236434d6c082cf0be14ba3e3246b25a153"),
        new("stmary5.tfm", 848, "052a74aa1f218573cb6e94100f78a7022c8db53c8078432d971000c3eb62f0c8"),
        new("stmary6.pfb", 27786, "b7bb88b8b71712f8bc648240df49a10826f988a695bcb8e357a9bf9835a93b59"),
        new("stmary6.tfm", 848, "67b95fb10e764df112161df5622ff8c3c637c59ced807c947b8ac2c12da17611"),
        new("stmary7.pfb", 28237, "d5a26237839a2e17a97d1c5c804a6c01be71c2c2bb25b9f34506734a0e8181de"),
        new("stmary7.tfm", 848, "0e62aadc2213ee9de5b82661b93c2ad386b62ee2a2e0fd6c95911a5c9c40c33d"),
        new("stmary8.pfb", 27873, "89b5630c45fe1f91327531faa8c33f68009f299e04681aba8ead9e0bbb514cd3"),
        new("stmary8.tfm", 848, "c19ff2d3555bef3ef361fa6d3bf69ab7907ff775a32cd11d7e086f9db96dcb23"),
        new("stmary9.pfb", 27392, "f904c6b43ea9fc2e78c3a13bbcf4999933d49840ad18e9abeb1c780d845ff6be"),
        new("stmary9.tfm", 848, "8db345096c71d0cb3bc9a32f69061e3088532eda711728b3a6143a1bb144f400"),
        new("stmaryrd.map", 214, "44f74dad233915a7a3fc43c51039c4eb165f6f8a4cd8e9a8633da0fade885ee7"),
        new("stmaryrd.sty", 11080, "75fb8a156eaad65a58ddaedb1556f86638a013e5eaa640f3bbd994a8ce630270"),
        new("tex-text.map", 802, "65a1cf61ec924a1a8b8494760586a098bbf68046f004339108556c6465f75ada"),
        new("tex-text.tec", 398, "511836c4d363daffa1c789956bdb44b89ac526ef3e4215a846c313b717447d40"),
        new("totcount.sty", 5857, "d6e30e018db29b709122085f88db0404d6f2ac1d31d83190b7ec55aec8d7d2be"),
        new("ubbm.fd", 2132, "293e6076b710eee2446216d87f9a219c220ddd250839ce7b60b32ffd22a678d2"),
        new("ubbmss.fd", 1919, "fafb93184dabfe70e4de8e8def946ef0a0571d3044be514780e3f1c40f60ee44"),
        new("ubbmtt.fd", 1586, "a94a043d2d0bf0f83694afed97e4560648ccab5e15481299e2655d00fe3642a0"),
    });

    public static bool MatchesComponents(IReadOnlyDictionary<string, NativeResourcePin> pins)
    {
        if (!Ready || pins.Count != ResourceCount || pins.Values.Sum(p => (long)p.Length) != TotalBytes) return false;
        if (!Fixed("dvi-map-converter.js", DviConverterLoaderLength, DviConverterLoaderHash)
            || !Fixed("dvi-map-converter.wasm", DviConverterWasmLength, DviConverterWasmHash)
            || !Fixed("xdv-map-converter.js", XeConverterLoaderLength, XeConverterLoaderHash)
            || !Fixed("xdv-map-converter.wasm", XeConverterWasmLength, XeConverterWasmHash)
            || !Fixed(NativeCompilerSuitePins.XeCatalogResourceName, XeCatalogLength, XeCatalogHash)
            || AdditionalResources.Any(p => !pins.TryGetValue(p.Name, out var actual) || actual != p)) return false;
        var previous = new Dictionary<string, NativeResourcePin>(pins, StringComparer.Ordinal);
        foreach (var p in AdditionalResources) previous.Remove(p.Name);
        Restore("dvi-map-converter.js", NativeCompilerOriginPins.DviConverterLoaderLength, NativeCompilerOriginPins.DviConverterLoaderHash);
        Restore("dvi-map-converter.wasm", NativeCompilerOriginPins.DviConverterWasmLength, NativeCompilerOriginPins.DviConverterWasmHash);
        Restore("xdv-map-converter.js", NativeCompilerOriginPins.XeConverterLoaderLength, NativeCompilerOriginPins.XeConverterLoaderHash);
        Restore("xdv-map-converter.wasm", NativeCompilerOriginPins.XeConverterWasmLength, NativeCompilerOriginPins.XeConverterWasmHash);
        // Restore the original77-face catalogue before checking the exact P15
        // closure. A newly claimed family cannot expand its parent's authority.
        Restore(NativeCompilerSuitePins.XeCatalogResourceName, ParentXeCatalogLength, ParentXeCatalogHash);
        return previous.Count == NativeCompilerOriginPins.ResourceCount
            && previous.Values.Sum(p => (long)p.Length) == NativeCompilerOriginPins.TotalBytes
            && NativeRuntimePackage.ResourceIdentity(previous.Values).Hex == NativeCompilerOriginPins.ClosureHash
            && NativeCompilerOriginPins.MatchesComponents(previous)
            && NativeRuntimePackage.ResourceIdentity(pins.Values).Hex == ClosureHash;
        bool Fixed(string name, int length, string hash) => pins.TryGetValue(name, out var p) && p == new NativeResourcePin(name, length, hash);
        void Restore(string name, int length, string hash) => previous[name] = new(name, length, hash);
    }
}
