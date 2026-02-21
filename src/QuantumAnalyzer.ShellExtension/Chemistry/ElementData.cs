using System.Collections.Generic;
using System.Drawing;

namespace QuantumAnalyzer.ShellExtension.Chemistry
{
    public static class ElementData
    {
        // Covalent radii in Angstroms (Alvarez 2008 / standard values)
        public static readonly Dictionary<string, double> CovalentRadius = new Dictionary<string, double>
        {
            { "H",  0.31 }, { "HE", 0.28 },
            { "LI", 1.28 }, { "BE", 0.96 }, { "B",  0.84 }, { "C",  0.77 },
            { "N",  0.71 }, { "O",  0.66 }, { "F",  0.57 }, { "NE", 0.58 },
            { "NA", 1.66 }, { "MG", 1.41 }, { "AL", 1.21 }, { "SI", 1.11 },
            { "P",  1.07 }, { "S",  1.05 }, { "CL", 1.02 }, { "AR", 1.06 },
            { "K",  2.03 }, { "CA", 1.76 },
            { "SC", 1.70 }, { "TI", 1.60 }, { "V",  1.53 }, { "CR", 1.39 },
            { "MN", 1.50 }, { "FE", 1.42 }, { "CO", 1.38 }, { "NI", 1.24 },
            { "CU", 1.32 }, { "ZN", 1.22 },
            { "GA", 1.22 }, { "GE", 1.20 }, { "AS", 1.19 }, { "SE", 1.20 },
            { "BR", 1.20 }, { "KR", 1.16 },
            { "RB", 2.20 }, { "SR", 1.95 }, { "Y",  1.90 }, { "ZR", 1.75 },
            { "NB", 1.64 }, { "MO", 1.54 }, { "TC", 1.47 }, { "RU", 1.46 },
            { "RH", 1.42 }, { "PD", 1.39 }, { "AG", 1.45 }, { "CD", 1.44 },
            { "IN", 1.42 }, { "SN", 1.39 }, { "SB", 1.39 }, { "TE", 1.38 },
            { "I",  1.39 }, { "XE", 1.40 },
            { "CS", 2.44 }, { "BA", 2.15 },
            { "LA", 2.07 }, { "CE", 2.04 }, { "PR", 2.03 }, { "ND", 2.01 },
            { "PT", 1.36 }, { "AU", 1.36 }, { "HG", 1.32 },
            { "PB", 1.45 }, { "BI", 1.46 },
        };

        // CPK colours (Corey–Pauling–Koltun standard)
        public static readonly Dictionary<string, Color> CpkColor = new Dictionary<string, Color>
        {
            { "H",  Color.FromArgb(255, 255, 255) },  // white
            { "HE", Color.FromArgb(217, 255, 255) },
            { "LI", Color.FromArgb(204, 128, 255) },
            { "BE", Color.FromArgb(194, 255,   0) },
            { "B",  Color.FromArgb(255, 181, 181) },
            { "C",  Color.FromArgb( 80,  80,  80) },  // dark grey
            { "N",  Color.FromArgb( 48,  80, 248) },  // blue
            { "O",  Color.FromArgb(255,  13,  13) },  // red
            { "F",  Color.FromArgb(144, 224,  80) },  // green
            { "NE", Color.FromArgb(179, 227, 245) },
            { "NA", Color.FromArgb(171,  92, 242) },
            { "MG", Color.FromArgb(138, 255,   0) },
            { "AL", Color.FromArgb(191, 166, 166) },
            { "SI", Color.FromArgb(240, 200, 160) },
            { "P",  Color.FromArgb(255, 128,   0) },  // orange
            { "S",  Color.FromArgb(255, 200,  50) },  // yellow
            { "CL", Color.FromArgb( 31, 240,  31) },  // green
            { "AR", Color.FromArgb(128, 209, 227) },
            { "K",  Color.FromArgb(143,  64, 212) },
            { "CA", Color.FromArgb( 61, 255,   0) },
            { "SC", Color.FromArgb(230, 230, 230) },
            { "TI", Color.FromArgb(191, 194, 199) },
            { "V",  Color.FromArgb(166, 166, 171) },
            { "CR", Color.FromArgb(138, 153, 199) },
            { "MN", Color.FromArgb(156, 122, 199) },
            { "FE", Color.FromArgb(224, 102,  51) },  // orange-rust
            { "CO", Color.FromArgb(240, 144, 160) },
            { "NI", Color.FromArgb( 80, 208,  80) },
            { "CU", Color.FromArgb(200, 128,  51) },  // copper
            { "ZN", Color.FromArgb(125, 128, 176) },
            { "GA", Color.FromArgb(194, 143, 143) },
            { "GE", Color.FromArgb(102, 143, 143) },
            { "AS", Color.FromArgb(189, 128, 227) },
            { "SE", Color.FromArgb(255, 161,   0) },
            { "BR", Color.FromArgb(166,  41,  41) },  // dark red
            { "KR", Color.FromArgb( 92, 184, 209) },
            { "RB", Color.FromArgb(112,  46, 176) },
            { "SR", Color.FromArgb(  0, 255,   0) },
            { "Y",  Color.FromArgb(148, 255, 255) },
            { "ZR", Color.FromArgb(148, 224, 224) },
            { "NB", Color.FromArgb(115, 194, 201) },
            { "MO", Color.FromArgb( 84, 181, 181) },
            { "RU", Color.FromArgb( 36, 143, 143) },
            { "RH", Color.FromArgb( 10, 125, 140) },
            { "PD", Color.FromArgb(  0, 105, 133) },
            { "AG", Color.FromArgb(192, 192, 192) },  // silver
            { "CD", Color.FromArgb(255, 217, 143) },
            { "IN", Color.FromArgb(166, 117, 115) },
            { "SN", Color.FromArgb(102, 128, 128) },
            { "SB", Color.FromArgb(158,  99,  181) },
            { "TE", Color.FromArgb(212, 122,   0) },
            { "I",  Color.FromArgb(148,   0, 148) },  // purple
            { "XE", Color.FromArgb( 66, 158, 176) },
            { "CS", Color.FromArgb( 87,  23, 143) },
            { "BA", Color.FromArgb(  0, 201,   0) },
            { "LA", Color.FromArgb( 70, 255, 255) },
            { "PT", Color.FromArgb(208, 208, 224) },
            { "AU", Color.FromArgb(255, 209,  35) },  // gold
            { "HG", Color.FromArgb(184, 184, 208) },
            { "PB", Color.FromArgb( 87,  89,  97) },
            { "BI", Color.FromArgb(158,  79, 181) },
        };

        // Atomic number → element symbol (1-based, covers Z=1..54 + common metals)
        private static readonly Dictionary<int, string> AtomicNumberToSymbol = new Dictionary<int, string>
        {
             {1,"H"},  {2,"He"}, {3,"Li"}, {4,"Be"}, {5,"B"},  {6,"C"},  {7,"N"},  {8,"O"},
             {9,"F"}, {10,"Ne"},{11,"Na"},{12,"Mg"},{13,"Al"},{14,"Si"},{15,"P"}, {16,"S"},
            {17,"Cl"},{18,"Ar"},{19,"K"}, {20,"Ca"},{21,"Sc"},{22,"Ti"},{23,"V"}, {24,"Cr"},
            {25,"Mn"},{26,"Fe"},{27,"Co"},{28,"Ni"},{29,"Cu"},{30,"Zn"},{31,"Ga"},{32,"Ge"},
            {33,"As"},{34,"Se"},{35,"Br"},{36,"Kr"},{37,"Rb"},{38,"Sr"},{39,"Y"}, {40,"Zr"},
            {41,"Nb"},{42,"Mo"},{43,"Tc"},{44,"Ru"},{45,"Rh"},{46,"Pd"},{47,"Ag"},{48,"Cd"},
            {49,"In"},{50,"Sn"},{51,"Sb"},{52,"Te"},{53,"I"}, {54,"Xe"},{55,"Cs"},{56,"Ba"},
            {57,"La"},{58,"Ce"},{59,"Pr"},{60,"Nd"},{61,"Pm"},{62,"Sm"},{63,"Eu"},{64,"Gd"},
            {65,"Tb"},{66,"Dy"},{67,"Ho"},{68,"Er"},{69,"Tm"},{70,"Yb"},{71,"Lu"},
            {72,"Hf"},{73,"Ta"},{74,"W"}, {75,"Re"},{76,"Os"},{77,"Ir"},{78,"Pt"},{79,"Au"},
            {80,"Hg"},{81,"Tl"},{82,"Pb"},{83,"Bi"},{84,"Po"},{85,"At"},{86,"Rn"},
        };

        public static string SymbolFromAtomicNumber(int z)
        {
            return AtomicNumberToSymbol.TryGetValue(z, out string sym) ? sym : "X";
        }

        public static double GetCovalentRadius(string element)
        {
            string key = element.ToUpperInvariant();
            return CovalentRadius.TryGetValue(key, out double r) ? r : 1.50;
        }

        public static Color GetCpkColor(string element)
        {
            string key = element.ToUpperInvariant();
            return CpkColor.TryGetValue(key, out Color c) ? c : Color.FromArgb(255, 20, 147);
        }
    }
}
