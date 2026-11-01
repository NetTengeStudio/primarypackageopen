using System.Collections.Generic;
using Barotrauma;
namespace PrimaryPackage.CrewManifest {
    internal static class SalaryData {
        public static readonly Dictionary<Identifier, int> Salary = new() {
            ["captain".ToIdentifier()] = 35,
            ["securityofficer".ToIdentifier()] = 22,
            ["engineer".ToIdentifier()] = 18,
            ["medicaldoctor".ToIdentifier()] = 18,
            ["mechanic".ToIdentifier()] = 15,
        };
        public static readonly Dictionary<Identifier, string> RoleName = new() {
            ["captain".ToIdentifier()] = "Капитан",
            ["securityofficer".ToIdentifier()] = "Офицер СБ",
            ["engineer".ToIdentifier()] = "Инженер",
            ["medicaldoctor".ToIdentifier()] = "Медработник",
            ["mechanic".ToIdentifier()] = "Механик",
        };
        public static readonly (int Value, string Id)[] Bills = {
            (500, "money500cr"),
            (100, "money100cr"),
            (50,  "money50cr"),
            (10,  "money10cr"),
            (5,   "money5cr"),
            (1,   "money1cr"),
        };
    }
}
