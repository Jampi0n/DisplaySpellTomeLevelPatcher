using System;
using System.Collections.Generic;
using System.Linq;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Synthesis;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.FormKeys.SkyrimSE;
using System.Threading.Tasks;
using System.Collections.Immutable;

namespace DisplaySpellTomeLevelPatcher {
    public static class Program {
        private static Lazy<Settings> _settings = null!;
        public static Task<int> Main(string[] args) {
            return SynthesisPipeline.Instance
                .AddPatch<ISkyrimMod, ISkyrimModGetter>(RunPatch)
                .SetAutogeneratedSettings(nickname: "Settings", path: "settings.json", out _settings)
                .SetTypicalOpen(GameRelease.SkyrimSE, "DisplaySpellTomeLevelPatcher.esp")
                .Run(args);
        }

        public readonly static ModKey BetterSpellLearning = ModKey.FromNameAndExtension("Better Spell Learning.esp");

        public const string levelFormatVariable = "<level>";
        public const string spellFormatVariable = "<spell>";
        public const string pluginFormatVariable = "<plugin>";
        public const string schoolFormatVariable = "<school>";

        public readonly static string[] levelNames = new string[] { "Novice", "Apprentice", "Adept", "Expert", "Master" };

        public readonly static Dictionary<ActorValue, string> magicSchools = new() {
            { ActorValue.Alteration, "Alteration" },
            { ActorValue.Conjuration, "Conjuration" },
            { ActorValue.Destruction, "Destruction" },
            { ActorValue.Illusion, "Illusion" },
            { ActorValue.Restoration, "Restoration" }
        };


        public static string GetSchoolName(ActorValue av) {
            if(magicSchools.ContainsKey(av)) {
                return magicSchools[av];
            } else {
                return "None";
            }
        }

        public static readonly HashSet<uint> AllowedMinimumSkillLevels = new() { 0, 25, 50, 75, 100 };
        public static Tuple<ActorValue, int>? GetSpellInfo(IPatcherState<ISkyrimMod, ISkyrimModGetter> state, ISpellGetter spell) {
            double maxCost = -1.0;
            int maxLevel = -1;
            IMagicEffectGetter? maxBaseEffect = null;
            foreach(var effect in spell.Effects) {
                if(effect.Data == null) {
                    continue;
                }
                if(effect.BaseEffect.TryResolve(state.LinkCache, out var baseEffect)) {
                    var durFactor = baseEffect.CastType == CastType.Concentration ? 1 : Math.Pow(effect.Data.Duration == 0 ? 10 : effect.Data.Duration, 1.1);
                    double cost = baseEffect.BaseCost * Math.Pow(effect.Data.Magnitude, 1.1) * durFactor;
                    if(cost > maxCost) {
                        maxCost = cost;
                        maxBaseEffect = baseEffect;

                        if(!AllowedMinimumSkillLevels.Contains(baseEffect.MinimumSkillLevel)) {
                            Console.WriteLine("    Unexpected minimum skill level for magic effect:" + baseEffect.FormKey);
                        }
                        maxLevel = (int)(baseEffect.MinimumSkillLevel / 25);
                        maxLevel = Math.Max(0, Math.Min(4, maxLevel));
                    }
                }
            }
            if(maxBaseEffect == null) {
                return null;
            }
            return new Tuple<ActorValue, int>(maxBaseEffect.MagicSkill, maxLevel);
        }

        public static ISpellGetter? GetSpellBetterSpellLearning(IPatcherState<ISkyrimMod, ISkyrimModGetter> state, IBookGetter book) {
            ISpellGetter? spell = null;
            if(book.VirtualMachineAdapter != null) {
                foreach(var script in book.VirtualMachineAdapter.Scripts.Where((IScriptEntryGetter script) => {
                    return script.Name == "SpellTomeReadScript";
                })) {
                    foreach(var property in script.Properties.Where((IScriptPropertyGetter property) => {
                        return property is IScriptObjectPropertyGetter && property.Name == "SpellLearned";
                    })) {
                        if(property is IScriptObjectPropertyGetter objectProperty) {
                            if(objectProperty.Object.TryResolve<ISpellGetter>(state.LinkCache, out var tmp)) {
                                spell = tmp;
                            }
                        }
                    }
                }
            }
            return spell;
        }

        public static void RunPatch(IPatcherState<ISkyrimMod, ISkyrimModGetter> state) {
            bool hasBetterSpellLearning = state.LoadOrder.PriorityOrder.HasMod(BetterSpellLearning);

            foreach(var bookContext in state.LoadOrder.PriorityOrder.Book().WinningContextOverrides()) {
                IBookGetter book = bookContext.Record;
                try {
                    if(book.Name?.String == null) {
                        continue;
                    }
                    if(!book.Keywords?.Contains(Skyrim.Keyword.VendorItemSpellTome) ?? true) {
                        continue;
                    }
                    ISpellGetter? spell = null;
                    if(book.Teaches is IBookSpellGetter teachedSpell) {
                        if(teachedSpell.Spell.TryResolve(state.LinkCache, out var tmp)) {
                            spell = tmp;
                        }
                    }
                    if(spell == null) {
                        if(hasBetterSpellLearning) {
                            spell = GetSpellBetterSpellLearning(state, book);
                        }
                    }
                    if(spell == null) {
                        continue;
                    }
                    if(spell.Name?.String == null) {
                        continue;
                    }

                    var spellName = spell.Name.String;

                    var spellInfo = GetSpellInfo(state, spell);

                    var renameFormat = _settings.Value.Format;

                    var schoolName = "";
                    var levelName = "";
                    var requiresSpellInfo = renameFormat.Contains(schoolFormatVariable) || renameFormat.Contains(levelFormatVariable);
                    if(requiresSpellInfo) {
                        if(spellInfo == null) {
                            Console.WriteLine("    Cannot determine school and level for book: " + book.Name.String);
                            continue;
                        }
                        var school = spellInfo.Item1;
                        schoolName = GetSchoolName(school);


                        var level = spellInfo.Item2;
                        levelName = levelNames[level];
                    }
                    var pluginName = book.FormKey.ModKey.Name.ToString();


                    var newName = renameFormat.Replace(levelFormatVariable, levelName).Replace(pluginFormatVariable, pluginName).Replace(schoolFormatVariable, schoolName).Replace(spellFormatVariable, spellName);

                    Console.WriteLine(book.Name.String + "->" + newName);

                    state.PatchMod.Books.GetOrAddAsOverride(book).Name = newName;
                } catch(Exception e) {
                    Console.WriteLine(RecordException.Enrich(e, bookContext.ModKey, book).ToString());
                }
            }
        }

    }
}
