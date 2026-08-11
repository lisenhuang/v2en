namespace v2en.Utilities;

/// <summary>
/// ISO-3166-1 alpha-2 country lookup: display name plus a representative point (a cartographic
/// LABEL point, not a raw centroid, so it lands on land rather than in the sea for horseshoe-shaped
/// countries). Used by the analytics dashboard to name and plot a visit when Cloudflare gave us a
/// country code but no city-level latitude/longitude (only some Cloudflare plans send those).
///
/// Data: Natural Earth 1:50m Admin 0 – Countries (naturalearthdata.com), PUBLIC DOMAIN.
/// The flag emoji is derived from the code itself (regional-indicator letters), so no icon assets.
/// </summary>
public static class Countries
{
    /// <summary>A country's display name and the map point used to plot it.</summary>
    public readonly record struct Country(string Name, double Latitude, double Longitude);

    /// <summary>Cloudflare sends this when it cannot geolocate the visitor at all.</summary>
    public const string UnknownCode = "XX";

    /// <summary>Cloudflare sends this for traffic arriving over the Tor network.</summary>
    public const string TorCode = "T1";

    private static readonly Dictionary<string, Country> Table = new(StringComparer.OrdinalIgnoreCase)
    {
        ["AD"] = new("Andorra", 42.5476, 1.5394),
        ["AE"] = new("United Arab Emirates", 23.4663, 54.5473),
        ["AF"] = new("Afghanistan", 34.1643, 66.4966),
        ["AG"] = new("Antigua and Barbuda", 17.3522, -61.7906),
        ["AI"] = new("Anguilla", 18.2430, -63.0264),
        ["AL"] = new("Albania", 40.6549, 20.1138),
        ["AM"] = new("Armenia", 40.4591, 44.8006),
        ["AO"] = new("Angola", -12.1828, 17.9842),
        ["AQ"] = new("Antarctica", -79.8432, 35.8855),
        ["AR"] = new("Argentina", -33.5012, -64.1733),
        ["AS"] = new("American Samoa", -14.3267, -170.7472),
        ["AT"] = new("Austria", 47.5189, 14.1305),
        ["AU"] = new("Australia", -24.1295, 134.0497),
        ["AW"] = new("Aruba", 12.5174, -69.9728),
        ["AX"] = new("Åland Islands", 60.1565, 19.8697),
        ["AZ"] = new("Azerbaijan", 40.4024, 47.2110),
        ["BA"] = new("Bosnia and Herzegovina", 44.0911, 18.0684),
        ["BB"] = new("Barbados", 13.1637, -59.5690),
        ["BD"] = new("Bangladesh", 24.2150, 89.6850),
        ["BE"] = new("Belgium", 50.7854, 4.8004),
        ["BF"] = new("Burkina Faso", 12.6730, -1.3639),
        ["BG"] = new("Bulgaria", 42.5088, 25.1571),
        ["BH"] = new("Bahrain", 26.0560, 50.5548),
        ["BI"] = new("Burundi", -3.3328, 29.9171),
        ["BJ"] = new("Benin", 10.3248, 2.3520),
        ["BL"] = new("Saint-Barthélemy", 17.9020, -62.8332),
        ["BM"] = new("Bermuda", 32.2966, -64.7636),
        ["BN"] = new("Brunei Darussalam", 4.4483, 114.5519),
        ["BO"] = new("Bolivia", -16.6660, -64.5934),
        ["BR"] = new("Brazil", -12.0987, -49.5594),
        ["BS"] = new("Bahamas", 26.4018, -77.1467),
        ["BT"] = new("Bhutan", 27.5367, 90.0403),
        ["BW"] = new("Botswana", -22.1026, 24.1792),
        ["BY"] = new("Belarus", 53.8219, 28.4177),
        ["BZ"] = new("Belize", 17.2021, -88.7130),
        ["CA"] = new("Canada", 60.3243, -101.9107),
        ["CD"] = new("Democratic Republic of the Congo", -1.8582, 23.4588),
        ["CF"] = new("Central African Republic", 6.9897, 20.9069),
        ["CG"] = new("Republic of the Congo", 0.1423, 15.9005),
        ["CH"] = new("Switzerland", 46.7191, 7.4640),
        ["CI"] = new("Côte d'Ivoire", 7.4914, -5.5686),
        ["CK"] = new("Cook Islands", -21.2160, -159.7857),
        ["CL"] = new("Chile", -38.1518, -72.3189),
        ["CM"] = new("Cameroon", 4.5850, 12.4735),
        ["CN"] = new("China", 32.4982, 106.3373),
        ["CO"] = new("Colombia", 3.3731, -73.1743),
        ["CR"] = new("Costa Rica", 10.0651, -84.0779),
        ["CU"] = new("Cuba", 21.3340, -77.9759),
        ["CV"] = new("Republic of Cabo Verde", 15.0748, -23.6394),
        ["CW"] = new("Curaçao", 12.1450, -68.9206),
        ["CY"] = new("Cyprus", 34.9133, 33.0842),
        ["CZ"] = new("Czech Republic", 49.8824, 15.3776),
        ["DE"] = new("Germany", 50.9617, 9.6783),
        ["DJ"] = new("Djibouti", 11.9763, 42.4988),
        ["DK"] = new("Denmark", 55.9670, 9.0182),
        ["DM"] = new("Dominica", 15.4588, -61.3450),
        ["DO"] = new("Dominican Republic", 19.1041, -70.6540),
        ["DZ"] = new("Algeria", 27.3974, 2.8082),
        ["EC"] = new("Ecuador", -1.2591, -78.1884),
        ["EE"] = new("Estonia", 58.7249, 25.8671),
        ["EG"] = new("Egypt", 26.1862, 29.4458),
        ["EH"] = new("Western Sahara", 23.9676, -12.6303),
        ["ER"] = new("Eritrea", 15.7874, 38.2856),
        ["ES"] = new("Spain", 40.0910, -3.4647),
        ["ET"] = new("Ethiopia", 8.0328, 39.0886),
        ["FI"] = new("Finland", 63.2524, 27.2764),
        ["FJ"] = new("Fiji", -17.8261, 177.9754),
        ["FK"] = new("Falkland Islands / Malvinas", -51.6089, -58.7386),
        ["FM"] = new("Federated States of Micronesia", 6.8876, 158.2340),
        ["FO"] = new("Faeroe Islands", 62.1856, -7.0584),
        ["FR"] = new("France", 46.6961, 2.5523),
        ["GA"] = new("Gabon", -0.4377, 11.8359),
        ["GB"] = new("United Kingdom", 54.4027, -2.1163),
        ["GD"] = new("Grenada", 12.1132, -61.6805),
        ["GE"] = new("Georgia", 41.8701, 43.7357),
        ["GG"] = new("Guernsey", 49.4635, -2.5617),
        ["GH"] = new("Ghana", 7.7176, -1.0369),
        ["GL"] = new("Greenland", 74.3194, -39.3353),
        ["GM"] = new("The Gambia", 13.6417, -14.9983),
        ["GN"] = new("Guinea", 10.6185, -10.0164),
        ["GQ"] = new("Equatorial Guinea", 2.3330, 8.9902),
        ["GR"] = new("Greece", 39.4928, 21.7257),
        ["GS"] = new("South Georgia and the Islands", -55.6834, -31.0632),
        ["GT"] = new("Guatemala", 14.9821, -90.4971),
        ["GU"] = new("Guam", 13.3542, 144.7036),
        ["GW"] = new("Guinea-Bissau", 12.1637, -14.5241),
        ["GY"] = new("Guyana", 5.1243, -58.9426),
        ["HK"] = new("Hong Kong", 22.4488, 114.0978),
        ["HM"] = new("Heard I. and McDonald Islands", -53.1035, 73.5052),
        ["HN"] = new("Honduras", 14.7948, -86.8876),
        ["HR"] = new("Croatia", 45.8058, 16.3724),
        ["HT"] = new("Haiti", 19.2638, -72.2241),
        ["HU"] = new("Hungary", 47.0868, 19.4479),
        ["ID"] = new("Indonesia", -0.9544, 101.8929),
        ["IE"] = new("Ireland", 53.0787, -7.7986),
        ["IL"] = new("Israel", 30.9111, 34.8479),
        ["IM"] = new("Isle of Man", 54.2208, -4.5301),
        ["IN"] = new("India", 22.6869, 79.3581),
        ["IO"] = new("British Indian Ocean Territory", -6.1908, 71.3483),
        ["IQ"] = new("Iraq", 33.0940, 43.2618),
        ["IR"] = new("Iran", 32.1662, 54.9315),
        ["IS"] = new("Iceland", 64.7793, -18.6737),
        ["IT"] = new("Italy", 44.7325, 11.0769),
        ["JE"] = new("Jersey", 49.2208, -2.0901),
        ["JM"] = new("Jamaica", 18.1371, -77.3188),
        ["JO"] = new("Jordan", 30.8050, 36.3760),
        ["JP"] = new("Japan", 36.1425, 138.4422),
        ["KE"] = new("Kenya", 0.5490, 37.9076),
        ["KG"] = new("Kyrgyzstan", 41.6685, 74.5326),
        ["KH"] = new("Cambodia", 12.6476, 104.5049),
        ["KI"] = new("Kiribati", 1.8204, -157.3846),
        ["KM"] = new("Comoros", -11.7277, 43.3181),
        ["KN"] = new("Saint Kitts and Nevis", 17.3366, -62.7580),
        ["KP"] = new("Dem. Rep. Korea", 39.8853, 126.4445),
        ["KR"] = new("Republic of Korea", 36.3849, 128.1295),
        ["KW"] = new("Kuwait", 29.4136, 47.3140),
        ["KY"] = new("Cayman Islands", 19.3199, -81.2405),
        ["KZ"] = new("Kazakhstan", 49.0541, 68.6855),
        ["LA"] = new("Lao PDR", 19.4318, 102.5339),
        ["LB"] = new("Lebanon", 34.1334, 35.9929),
        ["LC"] = new("Saint Lucia", 13.8924, -60.9801),
        ["LI"] = new("Liechtenstein", 47.1114, 9.5594),
        ["LK"] = new("Sri Lanka", 7.5811, 80.7048),
        ["LR"] = new("Liberia", 6.4472, -9.4604),
        ["LS"] = new("Lesotho", -29.4802, 28.2466),
        ["LT"] = new("Lithuania", 55.1037, 24.0899),
        ["LU"] = new("Luxembourg", 49.7337, 6.0776),
        ["LV"] = new("Latvia", 57.0669, 25.4587),
        ["LY"] = new("Libya", 26.6389, 18.0110),
        ["MA"] = new("Morocco", 31.6507, -7.1873),
        ["MC"] = new("Monaco", 43.7397, 7.3983),
        ["MD"] = new("Moldova", 47.4350, 28.4879),
        ["ME"] = new("Montenegro", 42.8031, 19.1437),
        ["MF"] = new("Saint-Martin", 18.0813, -63.0494),
        ["MG"] = new("Madagascar", -18.6283, 46.7042),
        ["MH"] = new("Marshall Islands", 7.0826, 171.1936),
        ["MK"] = new("North Macedonia", 41.5582, 21.5558),
        ["ML"] = new("Mali", 18.6927, -2.0385),
        ["MM"] = new("Myanmar", 21.5739, 95.8045),
        ["MN"] = new("Mongolia", 45.9975, 104.1504),
        ["MO"] = new("Macao", 22.1297, 113.5560),
        ["MP"] = new("Northern Mariana Islands", 15.1882, 145.7344),
        ["MR"] = new("Mauritania", 19.5871, -9.7403),
        ["MS"] = new("Montserrat", 16.7372, -62.1883),
        ["MT"] = new("Malta", 35.8929, 14.4330),
        ["MU"] = new("Mauritius", -20.2995, 57.5658),
        ["MV"] = new("Maldives", 4.1744, 73.5076),
        ["MW"] = new("Malawi", -13.3867, 33.6081),
        ["MX"] = new("Mexico", 23.9200, -102.2894),
        ["MY"] = new("Malaysia", 2.5287, 113.8371),
        ["MZ"] = new("Mozambique", -13.9432, 37.8379),
        ["NA"] = new("Namibia", -20.5753, 17.1082),
        ["NC"] = new("New Caledonia", -21.0647, 165.0840),
        ["NE"] = new("Niger", 17.4462, 9.5044),
        ["NF"] = new("Norfolk Island", -29.0330, 167.9545),
        ["NG"] = new("Nigeria", 9.4398, 7.5032),
        ["NI"] = new("Nicaragua", 12.6707, -85.0693),
        ["NL"] = new("Netherlands", 52.4222, 5.6114),
        ["NO"] = new("Norway", 61.3571, 9.6800),
        ["NP"] = new("Nepal", 28.2979, 83.6399),
        ["NR"] = new("Nauru", -0.5203, 166.9326),
        ["NU"] = new("Niue", -19.0460, -169.8626),
        ["NZ"] = new("New Zealand", -39.7590, 172.7870),
        ["OM"] = new("Oman", 22.1204, 57.3366),
        ["PA"] = new("Panama", 8.7220, -80.3521),
        ["PE"] = new("Peru", -12.9767, -72.9002),
        ["PF"] = new("French Polynesia", -17.6281, -149.4616),
        ["PG"] = new("Papua New Guinea", -5.6953, 143.9102),
        ["PH"] = new("Philippines", 11.1980, 122.4650),
        ["PK"] = new("Pakistan", 29.3284, 68.5456),
        ["PL"] = new("Poland", 51.9903, 19.4905),
        ["PM"] = new("Saint Pierre and Miquelon", 47.0403, -56.3324),
        ["PN"] = new("Pitcairn Islands", -24.3646, -128.3175),
        ["PR"] = new("Puerto Rico", 18.2347, -66.4811),
        ["PS"] = new("Palestine", 32.0474, 35.2913),
        ["PT"] = new("Portugal", 39.6067, -8.2718),
        ["PW"] = new("Palau", 7.5183, 134.5802),
        ["PY"] = new("Paraguay", -21.6745, -60.1464),
        ["QA"] = new("Qatar", 25.2374, 51.1435),
        ["RO"] = new("Romania", 45.7332, 24.9726),
        ["RS"] = new("Serbia", 44.1899, 20.7880),
        ["RU"] = new("Russian Federation", 58.2494, 44.6865),
        ["RW"] = new("Rwanda", -1.8972, 30.1039),
        ["SA"] = new("Saudi Arabia", 23.8069, 44.6996),
        ["SB"] = new("Solomon Islands", -8.0295, 159.1705),
        ["SC"] = new("Seychelles", -4.6767, 55.4802),
        ["SD"] = new("Sudan", 16.3307, 29.2607),
        ["SE"] = new("Sweden", 65.8592, 19.0171),
        ["SG"] = new("Singapore", 1.3666, 103.8169),
        ["SH"] = new("Saint Helena", -15.9505, -5.7126),
        ["SI"] = new("Slovenia", 46.0608, 14.9153),
        ["SK"] = new("Slovakia", 48.7340, 19.0499),
        ["SL"] = new("Sierra Leone", 8.6174, -11.7637),
        ["SM"] = new("San Marino", 43.9339, 12.4412),
        ["SN"] = new("Senegal", 15.1381, -14.7786),
        ["SO"] = new("Somalia", 3.5689, 45.1924),
        ["SR"] = new("Suriname", 4.1440, -55.9109),
        ["SS"] = new("South Sudan", 7.2305, 30.3902),
        ["ST"] = new("São Tomé and Principe", 0.9709, 7.0210),
        ["SV"] = new("El Salvador", 13.6854, -88.8901),
        ["SX"] = new("Sint Maarten", 18.0409, -63.0701),
        ["SY"] = new("Syria", 35.0066, 38.2778),
        ["SZ"] = new("Kingdom of eSwatini", -26.5337, 31.4673),
        ["TC"] = new("Turks and Caicos Islands", 21.8166, -71.7527),
        ["TD"] = new("Chad", 15.1430, 18.6450),
        ["TF"] = new("French Southern and Antarctic Lands", -49.3037, 69.1221),
        ["TG"] = new("Togo", 8.8072, 1.0581),
        ["TH"] = new("Thailand", 15.4597, 101.0732),
        ["TJ"] = new("Tajikistan", 38.1998, 72.5873),
        ["TL"] = new("Timor-Leste", -8.8037, 125.8547),
        ["TM"] = new("Turkmenistan", 39.8552, 58.6766),
        ["TN"] = new("Tunisia", 33.6873, 9.0079),
        ["TO"] = new("Tonga", -21.2100, -175.1630),
        ["TR"] = new("Turkey", 39.3454, 34.5083),
        ["TT"] = new("Trinidad and Tobago", 10.9989, -60.9184),
        ["TV"] = new("Tuvalu", -8.5137, 179.2096),
        ["TW"] = new("Taiwan", 23.6524, 120.8682),
        ["TZ"] = new("Tanzania", -6.0519, 34.9592),
        ["UA"] = new("Ukraine", 49.7247, 32.1409),
        ["UG"] = new("Uganda", 1.9726, 32.9486),
        ["US"] = new("United States", 39.5385, -97.4826),
        ["UY"] = new("Uruguay", -32.9611, -55.9669),
        ["UZ"] = new("Uzbekistan", 41.6936, 64.0054),
        ["VA"] = new("Vatican", 41.9033, 12.4534),
        ["VC"] = new("Saint Vincent and the Grenadines", 13.0879, -61.3359),
        ["VE"] = new("Venezuela", 7.1825, -64.5994),
        ["VG"] = new("British Virgin Islands", 18.4266, -64.6366),
        ["VI"] = new("United States Virgin Islands", 17.7467, -64.7792),
        ["VN"] = new("Vietnam", 21.7154, 105.3873),
        ["VU"] = new("Vanuatu", -15.3715, 166.9088),
        ["WF"] = new("Wallis and Futuna Islands", -14.2864, -178.1374),
        ["WS"] = new("Samoa", -13.6391, -172.4382),
        ["XK"] = new("Kosovo", 42.5936, 20.8607),
        ["YE"] = new("Yemen", 15.3282, 45.8744),
        ["ZA"] = new("South Africa", -29.7088, 23.6657),
        ["ZM"] = new("Zambia", -14.6608, 26.3953),
        ["ZW"] = new("Zimbabwe", -18.9116, 29.9254),

        // ── Cloudflare pseudo-codes (not ISO countries; they have no map point) ──
        ["T1"] = new("Tor network", double.NaN, double.NaN),
        ["EU"] = new("Europe (unspecified)", 50.0000, 10.0000),
        ["AP"] = new("Asia/Pacific (unspecified)", 10.0000, 110.0000),
    };

    /// <summary>True when <paramref name="code"/> is a code we can name and (usually) place.</summary>
    public static bool IsKnown(string? code) => code is not null && Table.ContainsKey(code);

    /// <summary>Display name for a code, or "Unknown" when we have no entry for it.</summary>
    public static string Name(string? code)
    {
        if (string.IsNullOrEmpty(code)) return "Unknown";
        return Table.TryGetValue(code, out var c) ? c.Name : code;
    }

    /// <summary>
    /// The point to plot this country at, or null when the code is unknown or is a Cloudflare
    /// pseudo-code with no meaningful location (Tor).
    /// </summary>
    public static (double Latitude, double Longitude)? Point(string? code)
    {
        if (string.IsNullOrEmpty(code) || !Table.TryGetValue(code, out var c)) return null;
        if (double.IsNaN(c.Latitude) || double.IsNaN(c.Longitude)) return null;
        return (c.Latitude, c.Longitude);
    }

    /// <summary>
    /// The flag emoji for an ISO alpha-2 code, built from the two regional-indicator symbols
    /// (U+1F1E6 + letter offset). Returns an empty string for anything that is not two ASCII
    /// letters, and for the Cloudflare pseudo-codes that have no flag.
    /// </summary>
    public static string FlagEmoji(string? code)
    {
        if (code is not { Length: 2 }) return "";
        if (code is TorCode or "EU" or "AP" or UnknownCode) return "";
        var a = char.ToUpperInvariant(code[0]);
        var b = char.ToUpperInvariant(code[1]);
        if (a is < 'A' or > 'Z' || b is < 'A' or > 'Z') return "";
        return char.ConvertFromUtf32(0x1F1E6 + (a - 'A')) + char.ConvertFromUtf32(0x1F1E6 + (b - 'A'));
    }
}
