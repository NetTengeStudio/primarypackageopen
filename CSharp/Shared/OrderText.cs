using System;
using Barotrauma;
namespace PrimaryPackage.Economy {
    public static class OrderText {
        private static bool Has(string key) {
            try {
                return TextManager.ContainsTag(key);
            } catch (Exception) {
                return false;
            }
        }
        public static string Get(string key, string fallback = null) {
            if (Has(key)) {
                return TextManager.Get(key).Value;
            }
            return fallback ?? key;
        }
        public static string Format(string key, string fallback, params (string Key, string Value)[] vars) {
            if (Has(key)) {
                return TextManager.GetWithVariables(key, vars).Value;
            }
            string result = fallback ?? key;
            if (result != null) {
                foreach ((string k, string v) in vars) {
                    result = result.Replace(k, v ?? "");
                }
            }
            return result;
        }
        public static string Format(string key, params (string Key, string Value)[] vars)
            => Format(key, key, vars);
    }
}
