using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using RimWorld;
using Verse;

namespace AutoTranslation
{
    public static class InjectionManager
    {
        internal static readonly ConcurrentBag<DefInjectionUtilityCustom.DefInjectionUntranslatedParams> defInjectedMissing =
            new ConcurrentBag<DefInjectionUtilityCustom.DefInjectionUntranslatedParams>();

        internal static readonly ConcurrentBag<KeyedUtility.KeyedReplacementParams> keyedMissing = new ConcurrentBag<KeyedUtility.KeyedReplacementParams>();

        internal static readonly ConcurrentDictionary<string, string> ReverseTranslator = new ConcurrentDictionary<string, string>();

        internal static readonly ConcurrentDictionary<string, (int, int)> TranslatedCountByPackageId = new ConcurrentDictionary<string, (int, int)>();

        internal static Dictionary<string, (int, int)> MissingCountByPackageId
        {
            get
            {
                if (_missingCountByPackageId == null)
                {
                    _missingCountByPackageId = new Dictionary<string, (int, int)>();
                    foreach (var id in defInjectedMissing.Select(x => x.def?.modContentPack?.PackageId ?? string.Empty))
                    {
                        if (!_missingCountByPackageId.ContainsKey(id)) _missingCountByPackageId[id] = (1, 0);
                        else _missingCountByPackageId[id] = (_missingCountByPackageId[id].Item1 + 1, 0);
                    }

                    foreach (var id in keyedMissing.Select(x => x.mod?.PackageId ?? string.Empty))
                    {
                        if (!_missingCountByPackageId.ContainsKey(id)) _missingCountByPackageId[id] = (0, 1);
                        else _missingCountByPackageId[id] = (_missingCountByPackageId[id].Item1, _missingCountByPackageId[id].Item2 + 1);
                    }
                }
                return _missingCountByPackageId;
            }
        }
        private static Dictionary<string, (int, int)> _missingCountByPackageId;

        internal static void UpdateTranslationCount(string packageId, bool isDef)
        {
            if (string.IsNullOrEmpty(packageId)) return;
            
            TranslatedCountByPackageId.AddOrUpdate(
                packageId,
                // 최초 추가 시
                isDef ? ((int, int))(1, 0) : ((int, int))(0, 1),
                // 기존 값 업데이트 시
                (_, old) => isDef ? 
                    ((int, int))(old.Item1 + 1, old.Item2) : 
                    ((int, int))(old.Item1, old.Item2 + 1)
            );
        }

        internal static void ResetTranslationCounts()
        {
            TranslatedCountByPackageId.Clear();
        }

        internal static Dictionary<string, (int, int, int, int)> GetTranslationStatsByPackageId()
        {
            var result = new Dictionary<string, (int, int, int, int)>();
            
            // MissingCountByPackageId에서 총 개수 가져오기
            foreach (var pair in MissingCountByPackageId)
            {
                if (!result.ContainsKey(pair.Key))
                {
                    result[pair.Key] = (0, 0, pair.Value.Item1, pair.Value.Item2);
                }
                else
                {
                    var existing = result[pair.Key];
                    result[pair.Key] = (existing.Item1, existing.Item2, pair.Value.Item1, pair.Value.Item2);
                }
            }
            
            // TranslatedCountByPackageId에서 완료된 개수 가져오기
            foreach (var pair in TranslatedCountByPackageId)
            {
                if (!result.ContainsKey(pair.Key))
                {
                    result[pair.Key] = (pair.Value.Item1, pair.Value.Item2, 0, 0);
                }
                else
                {
                    var existing = result[pair.Key];
                    result[pair.Key] = (pair.Value.Item1, pair.Value.Item2, existing.Item3, existing.Item4);
                }
            }
            
            return result;
        }

        internal static void InjectMissingDefInjection()
        {
            if (defInjectedMissing.Count == 0)
            {
                DefInjectionUtilityCustom.FindMissingDefInjection((@params =>
                {
                    if (@params.field.Name.ToLower().Contains("path")) return;

                    defInjectedMissing.Add(@params);
                    InjectMissingDefInjection(@params);
                }));
            }
            else
            {
                foreach (var injection in defInjectedMissing)
                {
                    InjectMissingDefInjection(injection);
                }
            }
        }

        internal static void InjectMissingDefInjection(ModContentPack targetMod)
        {
            if (defInjectedMissing.Count == 0) return;
            foreach (var param in defInjectedMissing.Where(x => x.def.modContentPack == targetMod))
            {
                InjectMissingDefInjection(param);
            }
        }
        
        internal static void InjectMissingDefInjection(DefInjectionUtilityCustom.DefInjectionUntranslatedParams @params)
        {
            if (!string.IsNullOrEmpty(@params.def?.modContentPack?.PackageId) &&
                    Settings.BlackListModPackageIds.Contains(@params.def?.modContentPack?.PackageId)) return;

            if (!@params.isCollection)
            {
                if (@params.translated != null)
                {
                    @params.InjectTranslation();
                    UpdateTranslationCount(@params.def?.modContentPack?.PackageId, true);
                    return;
                }
                TranslatorManager.Translate(@params.original, t =>
                {
                    @params.translated = t;
                    @params.InjectTranslation();
                    UpdateTranslationCount(@params.def?.modContentPack?.PackageId, true);

                    if (string.IsNullOrEmpty(t)) return;
                    ReverseTranslator[t] = @params.original;
                });
            }
            else
            {
                if (@params.originalCollection.Count == @params.translatedCollection.Count)
                {
                    @params.InjectTranslation();
                    foreach(var _ in @params.originalCollection)
                    {
                        UpdateTranslationCount(@params.def?.modContentPack?.PackageId, true);
                    }
                    return;
                }
                foreach (var original in @params.originalCollection)
                {
                    if (original.Contains("->"))
                    {
                        var token = original.Split(new[] { "->" }, StringSplitOptions.None);
                        var key = token[0];
                        var (value, placeHolders) = token[1].ToFormatString();
                        TranslatorManager.Translate(value, key + placeHolders.ToLineList(), t =>
                        {
                            string t2 = string.Empty;
                            try
                            {
                                t = t.FitFormat(placeHolders.Count);
                                t2 = key + "->" + string.Format(t, placeHolders.ToArray());
                                if (!@params.translatedCollection.TryAdd(original, t2))
                                {
                                }
                            }
                            catch (Exception e)
                            {
                                Log.WarningOnce(
                                    AutoTranslation.LogPrefix +
                                    $"Formating failed: {key}:{value} => {t}, {placeHolders.Count}, reason {e.Message}",
                                    value.GetHashCode());
                                @params.translatedCollection.TryAdd(original, original);
                                TranslatorManager.CachedTranslations.TryRemove(value, out _);
                            }

                            @params.InjectTranslation();
                            UpdateTranslationCount(@params.def?.modContentPack?.PackageId, true);

                            if (string.IsNullOrEmpty(t2)) return;
                            ReverseTranslator[t2] = original;
                        });
                    }
                }
            }
        }

        internal static void InjectMissingKeyed()
        {
            if (LanguageDatabase.activeLanguage == LanguageDatabase.defaultLanguage) return;

            if (keyedMissing.Count == 0)
            {
                foreach (var @params in KeyedUtility.FindMissingKeyed())
                {
                    keyedMissing.Add(@params);
                }
            }

            foreach (var @param in keyedMissing)
            {
                if (@param.mod?.PackageId != null && Settings.BlackListModPackageIds.Contains(@param.mod.PackageId)) continue;
                TranslatorManager.Translate(@param.value.value, @param.key, t =>
                {
                    ReverseTranslator[t] = @param.value.value;
                    @param.translation = t;
                    @param.Inject();
                    UpdateTranslationCount(@param.mod?.PackageId, false);
                });
            }
        }

        internal static void InjectMissingKeyed(ModContentPack targetMod)
        {
            if (LanguageDatabase.activeLanguage == LanguageDatabase.defaultLanguage) return;
            if (keyedMissing.Count == 0) return;

            foreach (var @param in keyedMissing.Where(x => x.mod == targetMod))
            {
                if (@param.mod?.PackageId != null && Settings.BlackListModPackageIds.Contains(@param.mod.PackageId)) continue;
                TranslatorManager.Translate(@param.value.value, @param.key, t =>
                {
                    ReverseTranslator[t] = @param.value.value;
                    @param.translation = t;
                    @param.Inject();
                    UpdateTranslationCount(@param.mod?.PackageId, false);
                });
            }
        }

        internal static void UndoInjectMissingDefInjection()
        {
            foreach (var param in defInjectedMissing)
            {
                param.UndoInject();
            }
        }

        internal static void UndoInjectMissingDefInjection(ModContentPack targetMod)
        {
            foreach (var param in defInjectedMissing.Where(x => x.def.modContentPack == targetMod))
            {
                param.UndoInject();
            }
        }

        internal static void UndoInjectMissingKeyed()
        {
            foreach (var @param in keyedMissing)
            {
                @param.UndoInject();
            }
        }

        internal static void UndoInjectMissingKeyed(ModContentPack targetMod)
        {
            foreach (var param in keyedMissing.Where(x => x.mod == targetMod))
            {
                param.UndoInject();
            }
        }

        internal static void InjectAll()
        {
            InjectMissingKeyed();
            InjectMissingDefInjection();
        }
        internal static void UndoInjectAll()
        {
            UndoInjectMissingDefInjection();
            UndoInjectMissingKeyed();
        }



        internal static void ClearDefInjectedTranslations()
        {
            foreach (var injection in defInjectedMissing)
            {
                injection.ClearTranslation();
            }
        }

        internal static IEnumerable<Type> defTypesTranslated
        {
            get
            {
                var hashSet = new HashSet<Type>();
                foreach (var @params in InjectionManager.defInjectedMissing)
                {
                    hashSet.Add(@params.defType);
                }
                return hashSet;
            }
        }

        public static void RetranslateMod(ModContentPack mod)
        {
            if (TranslatorManager._queue.Count > 0)
            {
                Messages.Message("AT_Message_RetranslateFailed".Translate(), MessageTypeDefOf.NegativeEvent);
                return;
            }

            // 해당 모드의 번역 카운트 초기화
            if (!string.IsNullOrEmpty(mod.PackageId))
            {
                TranslatedCountByPackageId.TryRemove(mod.PackageId, out _);
            }

            // 모드의 번역을 제거하고 캐시에서 해당 모드의 번역을 삭제
            UndoInjectMissingDefInjection(mod);
            UndoInjectMissingKeyed(mod);

            // 캐시에서 해당 모드와 관련된 항목 제거
            string modPrefix = $"{mod.PackageId}:";
            var keysToRemove = TranslatorManager.CachedTranslations.Keys
                .Where(k => k.StartsWith(modPrefix))
                .ToList();

            foreach (var key in keysToRemove)
            {
                if (TranslatorManager.CachedTranslations.TryRemove(key, out _))
                    TranslatorManager._cacheCount--;
            }

            // 캐시 저장
            CacheFileTool.Export(nameof(TranslatorManager.CachedTranslations),
                new Dictionary<string, string>(TranslatorManager.CachedTranslations));

            // 모드 번역 다시 주입
            InjectMissingDefInjection(mod);
            InjectMissingKeyed(mod);

            Messages.Message("AT_Message_RetranslateSuccess".Translate(mod.Name), MessageTypeDefOf.PositiveEvent);
        }
    }
}
