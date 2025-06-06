﻿using commonItems;
using commonItems.Collections;
using commonItems.Colors;
using commonItems.Localization;
using commonItems.Mods;
using ImperatorToCK3.CK3.Characters;
using ImperatorToCK3.CK3.Cultures;
using ImperatorToCK3.CK3.Provinces;
using ImperatorToCK3.CK3.Religions;
using ImperatorToCK3.CommonUtils;
using ImperatorToCK3.CommonUtils.Map;
using ImperatorToCK3.Imperator.Countries;
using ImperatorToCK3.Imperator.Diplomacy;
using ImperatorToCK3.Imperator.Jobs;
using ImperatorToCK3.Mappers.CoA;
using ImperatorToCK3.Mappers.Culture;
using ImperatorToCK3.Mappers.Government;
using ImperatorToCK3.Mappers.Nickname;
using ImperatorToCK3.Mappers.Province;
using ImperatorToCK3.Mappers.Region;
using ImperatorToCK3.Mappers.Religion;
using ImperatorToCK3.Mappers.SuccessionLaw;
using ImperatorToCK3.Mappers.TagTitle;
using Open.Collections;
using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace ImperatorToCK3.CK3.Titles;

internal sealed partial class Title {
	private readonly LandedTitles parentCollection;

	// This is a recursive class that scrapes common/landed_titles looking for title colors, landlessness,
	// and most importantly relation between baronies and barony provinces so we can link titles to actual clay.
	// Since titles are nested according to hierarchy we do this recursively.
	public sealed class LandedTitles : TitleCollection {
		public Dictionary<string, object> Variables { get; } = [];
	
		public IEnumerable<Title> Counties => this.Where(t => t.Rank == TitleRank.county);

		public void LoadTitles(ModFilesystem ck3ModFS, CK3LocDB ck3LocDB) {
			Logger.Info("Loading landed titles...");

			var parser = new Parser();
			RegisterKeys(parser);
			parser.ParseGameFolder("common/landed_titles", ck3ModFS, "txt", recursive: true, logFilePaths: true);
			LogIgnoredTokens();
			
			// Make sure every county has an adjective.
			foreach (var county in Counties) {
				string adjLocKey = county.Id + "_adj";

				// Use the name loc as the adjective loc.
				if (!ck3LocDB.TryGetValue(county.Id, out var nameLoc)) {
					continue;
				}
				foreach (var language in ConverterGlobals.SupportedLanguages) {
					if (ck3LocDB.HasKeyLocForLanguage(adjLocKey, language)) {
						continue;
					}
					
					ck3LocDB.AddLocForLanguage(adjLocKey, language, nameLoc[language] ?? nameLoc[ConverterGlobals.PrimaryLanguage] ?? county.Id);
				}
			}

			// Cleanup for counties having "capital" entries (found in TFE).
			foreach (var county in Counties) {
				if (county.CapitalCountyId is null) {
					continue;
				}

				Logger.Debug($"Removing capital entry from county {county.Id}.");
				county.CapitalCountyId = null;
			}

			// Cleanup for titles having invalid capital counties.
			var validTitleIds = this.Select(t => t.Id).ToFrozenSet();
			var placeholderCountyId = validTitleIds.Order().First(t => t.StartsWith("c_"));
			foreach (var title in this.Where(t => t.Rank > TitleRank.county)) {
				if (title.CapitalCountyId is null && !title.Landless) {
					// For landed titles, the game will generate capitals.
					continue;
				}
				if (title.CapitalCountyId is not null && validTitleIds.Contains(title.CapitalCountyId)) {
					continue;
				}
				// Try to use the first valid capital of a de jure vassal.
				string? newCapitalId;
				if (title.Rank >= TitleRank.kingdom) {
					newCapitalId = title.DeJureVassals
						.Select(v => v.CapitalCountyId)
						.FirstOrDefault(vassalCapitalId => vassalCapitalId is not null && validTitleIds.Contains(vassalCapitalId));
				} else {
					newCapitalId = title.DeJureVassals
						.Where(v => v.Rank == TitleRank.county)
						.Select(c => c.Id)
						.FirstOrDefault();
				}
				
				// If not found, for landless titles try using capital of de jure liege.
				if (newCapitalId is null && title.Landless) {
					newCapitalId = title.DeJureLiege?.CapitalCountyId;
				}
				if (newCapitalId is not null) {
					Logger.Debug($"Title {title.Id} has invalid capital county {title.CapitalCountyId ?? "NULL"}, replacing it with {newCapitalId}.");
					title.CapitalCountyId = newCapitalId;
				} else {
					Logger.Warn($"Using placeholder county as capital for title {title.Id} with invalid capital county {title.CapitalCountyId ?? "NULL"}.");
					title.CapitalCountyId = placeholderCountyId;
				}
			}

			Logger.IncrementProgress();
		}
		public void LoadTitles(BufferedReader reader) {
			var parser = new Parser();
			RegisterKeys(parser);
			parser.ParseStream(reader);

			LogIgnoredTokens();
		}
		public void LoadStaticTitles() {
			Logger.Info("Loading static landed titles...");

			var parser = new Parser();
			RegisterKeys(parser);

			parser.ParseFile("configurables/static_landed_titles.txt");

			LogIgnoredTokens();

			Logger.IncrementProgress();
		}

		public void CarveTitles(LandedTitles overrides) {
			Logger.Debug("Carving titles...");
			// merge in new king and empire titles into this from overrides, overriding duplicates
			foreach (var overrideTitle in overrides.Where(t => t.Rank > TitleRank.duchy)) {
				// inherit vanilla vassals
				TryGetValue(overrideTitle.Id, out Title? vanillaTitle);
				AddOrReplace(new Title(vanillaTitle, overrideTitle, this));
			}

			// update duchies to correct de jure liege, remove de jure titles that lose all de jure vassals
			foreach (var title in overrides.Where(t => t.Rank == TitleRank.duchy)) {
				if (!TryGetValue(title.Id, out Title? duchy)) {
					Logger.Warn($"Duchy {title.Id} not found!");
					continue;
				}
				if (duchy.DeJureLiege is not null) {
					if (duchy.DeJureLiege.DeJureVassals.Count <= 1) {
						duchy.DeJureLiege.DeJureLiege = null;
					}
				}
				duchy.DeJureLiege = title.DeJureLiege;
			}
		}

		private void RegisterKeys(Parser parser) {
			parser.RegisterRegex(CommonRegexes.Variable, (reader, variableName) => {
				var variableValue = reader.GetString();
				Variables[variableName[1..]] = variableValue;
			});
			parser.RegisterRegex(Regexes.TitleId, (reader, titleNameStr) => {
				// Pull the titles beneath this one and add them to the lot.
				// A title can be defined in multiple files, in that case merge the definitions.
				if (TryGetValue(titleNameStr, out var titleToUpdate)) {
					titleToUpdate.LoadTitles(reader);
				} else {
					var newTitle = Add(titleNameStr);
					newTitle.LoadTitles(reader);
				}
			});
			parser.IgnoreAndLogUnregisteredItems();
		}

		private static void LogIgnoredTokens() {
			if (Title.IgnoredTokens.Count > 0) {
				Logger.Warn($"Ignored title tokens: {Title.IgnoredTokens}");
			}
		}

		public Title Add(string id) {
			if (string.IsNullOrEmpty(id)) {
				throw new ArgumentException("Not inserting a Title with empty id!");
			}

			var newTitle = new Title(this, id);
			dict[newTitle.Id] = newTitle;
			return newTitle;
		}

		internal Title Add(
			Country country,
			Dependency? dependency,
			CountryCollection imperatorCountries,
			LocDB irLocDB,
			CK3LocDB ck3LocDB,
			ProvinceMapper provinceMapper,
			CoaMapper coaMapper,
			TagTitleMapper tagTitleMapper,
			GovernmentMapper governmentMapper,
			SuccessionLawMapper successionLawMapper,
			DefiniteFormMapper definiteFormMapper,
			ReligionMapper religionMapper,
			CultureMapper cultureMapper,
			NicknameMapper nicknameMapper,
			CharacterCollection characters,
			Date conversionDate,
			Configuration config,
			IReadOnlyCollection<string> enabledCK3Dlcs
		) {
			var newTitle = new Title(this,
				country,
				dependency,
				imperatorCountries,
				irLocDB,
				ck3LocDB,
				provinceMapper,
				coaMapper,
				tagTitleMapper,
				governmentMapper,
				successionLawMapper,
				definiteFormMapper,
				religionMapper,
				cultureMapper,
				nicknameMapper,
				characters,
				conversionDate,
				config,
				enabledCK3Dlcs
			);
			dict[newTitle.Id] = newTitle;
			return newTitle;
		}

		internal Title Add(
			string id,
			Governorship governorship,
			Country country,
			Imperator.Provinces.ProvinceCollection irProvinces,
			Imperator.Characters.CharacterCollection imperatorCharacters,
			bool regionHasMultipleGovernorships,
			LocDB irLocDB,
			CK3LocDB ck3LocDB,
			ProvinceMapper provinceMapper,
			CoaMapper coaMapper,
			DefiniteFormMapper definiteFormMapper,
			ImperatorRegionMapper imperatorRegionMapper,
			Configuration config
		) {
			var newTitle = new Title(this,
				id,
				governorship,
				country,
				irProvinces,
				imperatorCharacters,
				regionHasMultipleGovernorships,
				irLocDB,
				ck3LocDB,
				provinceMapper,
				coaMapper,
				definiteFormMapper,
				imperatorRegionMapper,
				config
			);
			dict[newTitle.Id] = newTitle;
			return newTitle;
		}
		public override void Remove(string name) {
			if (dict.TryGetValue(name, out var titleToErase)) {
				titleToErase.DeJureLiege = null; // Remove two-way liege-vassal link.

				foreach (var vassal in titleToErase.DeJureVassals) {
					vassal.DeJureLiege = null;
				}

				foreach (var title in this) {
					title.RemoveDeFactoLiegeReferences(name);
				}

				if (titleToErase.ImperatorCountry is not null) {
					titleToErase.ImperatorCountry.CK3Title = null;
				}
			}
			dict.Remove(name);
		}
		public Title? GetCountyForProvince(ulong provinceId) {
			foreach (var county in this.Where(title => title.Rank == TitleRank.county)) {
				if (county.CountyProvinceIds.Contains(provinceId)) {
					return county;
				}
			}
			return null;
		}

		public Title? GetBaronyForProvince(ulong provinceId) {
			var baronies = this.Where(title => title.Rank == TitleRank.barony);
			return baronies.FirstOrDefault(b => provinceId == b?.ProvinceId, defaultValue: null);
		}

		public ImmutableHashSet<string> GetHolderIdsForAllTitlesExceptNobleFamilyTitles(Date date) {
			return this
				.Where(t => t.NobleFamily != true)
				.Select(t => t.GetHolderId(date)).ToImmutableHashSet();
		}
		public ImmutableHashSet<string> GetAllHolderIds() {
			return this.SelectMany(t => t.GetAllHolderIds()).ToImmutableHashSet();
		}

		public void CleanUpHistory(CharacterCollection characters, Date ck3BookmarkDate) {
			Logger.Debug("Cleaning up title history...");
			
			// Remove invalid holder ID entries.
			var validCharacterIds = characters.Select(c => c.Id).ToImmutableHashSet();
			Parallel.ForEach(this, title => {
				if (!title.History.Fields.TryGetValue("holder", out var holderField)) {
					return;
				}

				holderField.RemoveAllEntries(
					value => value.ToString()?.RemQuotes() is string valStr && valStr != "0" && !validCharacterIds.Contains(valStr)
				);

				// Afterwards, remove empty date entries.
				holderField.DateToEntriesDict.RemoveWhere(kvp => kvp.Value.Count == 0);
			});

			// Fix holder being born after receiving the title, by moving the title grant to the birth date.
			Parallel.ForEach(this, title => {
				if (!title.History.Fields.TryGetValue("holder", out var holderField)) {
					return;
				}

				foreach (var (date, entriesList) in holderField.DateToEntriesDict.ToArray()) {
					if (date > ck3BookmarkDate) {
						continue;
					}

					var lastEntry = entriesList[^1];
					var holderId = lastEntry.Value.ToString()?.RemQuotes();
					if (holderId is null || holderId == "0") {
						continue;
					}

					if (!characters.TryGetValue(holderId, out var holder)) {
						holderField.DateToEntriesDict.Remove(date);
						continue;
					}

					var holderBirthDate = holder.BirthDate;
					if (date <= holderBirthDate) {
						// Move the title grant to the birth date.
						holderField.DateToEntriesDict.Remove(date);
						holderField.AddEntryToHistory(holderBirthDate, lastEntry.Key, lastEntry.Value);
					}
				}
			});
			
			// For counties, remove holder = 0 entries that precede a holder = <char ID> entry
			// that's before or at the bookmark date.
			Parallel.ForEach(Counties, county => {
				if (!county.History.Fields.TryGetValue("holder", out var holderField)) {
					return;
				}
				
				var holderIdAtBookmark = county.GetHolderId(ck3BookmarkDate);
				if (holderIdAtBookmark == "0") {
					return;
				}
				
				// If we have a holder at the bookmark date, remove all holder = 0 entries that precede it.
				var entryDatesToRemove = holderField.DateToEntriesDict
					.Where(pair => pair.Key < ck3BookmarkDate && pair.Value.Exists(v => v.Value.ToString() == "0"))
					.Select(pair => pair.Key)
					.ToArray();
				foreach (var date in entryDatesToRemove) {
					holderField.DateToEntriesDict.Remove(date);
				}
			});

			// Remove liege entries of the same rank as the title they're in.
			// For example, TFE had more or less this: d_kordofan = { liege = d_kordofan }
			var validRankChars = new HashSet<char> { 'e', 'k', 'd', 'c', 'b'};
			Parallel.ForEach(this, title => {
				if (!title.History.Fields.TryGetValue("liege", out var liegeField)) {
					return;
				}

				var titleRank = title.Rank;

				liegeField.RemoveAllEntries(value => {
					string? valueStr = value.ToString()?.RemQuotes();
					if (valueStr is null || valueStr == "0") {
						return false;
					}

					char rankChar = valueStr[0];
					if (!validRankChars.Contains(rankChar)) {
						Logger.Warn($"Removing invalid rank liege entry from {title.Id}: {valueStr}");
						return true;
					}
					
					var liegeRank = TitleRankUtils.CharToTitleRank(rankChar);
					if (liegeRank <= titleRank) {
						Logger.Warn($"Removing invalid rank liege entry from {title.Id}: {valueStr}");
						return true;
					}

					return false;
				});
			});
			
			// Remove liege entries that are not valid (liege title is not held at the entry date).
			foreach (var title in this) {
				if (!title.History.Fields.TryGetValue("liege", out var liegeField)) {
					continue;
				}

				foreach (var (date, entriesList) in liegeField.DateToEntriesDict.ToArray()) {
					if (entriesList.Count == 0) {
						continue;
					}
					
					var lastEntry = entriesList[^1];
					var liegeTitleId = lastEntry.Value.ToString()?.RemQuotes();
					if (liegeTitleId is null || liegeTitleId == "0") {
						continue;
					}

					if (!TryGetValue(liegeTitleId, out var liegeTitle)) {
						liegeField.DateToEntriesDict.Remove(date);
					} else if (liegeTitle.GetHolderId(date) == "0") {
						// Instead of removing the liege entry, see if the liege title has a holder at a later date,
						// and move the liege entry to that date.
						liegeTitle.History.Fields.TryGetValue("holder", out var liegeHolderField);
						Date? laterDate = liegeHolderField?.DateToEntriesDict
							.Where(kvp => kvp.Key > date && kvp.Key <= ck3BookmarkDate && kvp.Value.Count != 0 && kvp.Value[^1].Value.ToString() != "0")
							.Min(kvp => kvp.Key);

						if (laterDate == null) {
							liegeField.DateToEntriesDict.Remove(date);
						} else {
							var (setter, value) = liegeField.DateToEntriesDict[date][^1];
							liegeField.DateToEntriesDict.Remove(date);
							liegeField.AddEntryToHistory(laterDate, setter, value);
						}
					}
				}
			}

			// Remove undated succession_laws entries; the game doesn't seem to like them.
			foreach (var title in this) {
				if (!title.History.Fields.TryGetValue("succession_laws", out var successionLawsField)) {
					continue;
				}

				successionLawsField.InitialEntries.RemoveAll(entry => true);
			}
		}

		internal void ImportImperatorCountries(
			CountryCollection imperatorCountries,
			IReadOnlyCollection<Dependency> dependencies,
			TagTitleMapper tagTitleMapper,
			LocDB irLocDB,
			CK3LocDB ck3LocDB,
			ProvinceMapper provinceMapper,
			CoaMapper coaMapper,
			GovernmentMapper governmentMapper,
			SuccessionLawMapper successionLawMapper,
			DefiniteFormMapper definiteFormMapper,
			ReligionMapper religionMapper,
			CultureMapper cultureMapper,
			NicknameMapper nicknameMapper,
			CharacterCollection characters,
			Date conversionDate,
			Configuration config,
			List<KeyValuePair<Country, Dependency?>> countyLevelCountries,
			IReadOnlyCollection<string> enabledCK3Dlcs
		) {
			Logger.Info("Importing Imperator countries...");

			// landedTitles holds all titles imported from CK3. We'll now overwrite some and
			// add new ones from Imperator tags.
			int counter = 0;
			
			// We don't need pirates, barbarians etc.
			var realCountries = imperatorCountries.Where(c => c.CountryType == CountryType.real).ToImmutableList();
			
			// Import independent countries first, then subjects.
			var independentCountries = realCountries.Where(c => dependencies.All(d => d.SubjectId != c.Id)).ToImmutableList();
			var subjects = realCountries.Except(independentCountries).ToImmutableList();
			
			foreach (var country in independentCountries) {
				ImportImperatorCountry(
					country,
					dependency: null,
					imperatorCountries,
					tagTitleMapper,
					irLocDB,
					ck3LocDB,
					provinceMapper,
					coaMapper,
					governmentMapper,
					successionLawMapper,
					definiteFormMapper,
					religionMapper,
					cultureMapper,
					nicknameMapper,
					characters,
					conversionDate,
					config,
					countyLevelCountries,
					enabledCK3Dlcs
				);
				++counter;
			}
			foreach (var country in subjects) {
				ImportImperatorCountry(
					country,
					dependency: dependencies.FirstOrDefault(d => d.SubjectId == country.Id),
					imperatorCountries,
					tagTitleMapper,
					irLocDB,
					ck3LocDB,
					provinceMapper,
					coaMapper,
					governmentMapper,
					successionLawMapper,
					definiteFormMapper,
					religionMapper,
					cultureMapper,
					nicknameMapper,
					characters,
					conversionDate,
					config,
					countyLevelCountries,
					enabledCK3Dlcs
				);
				++counter;
			}
			Logger.Info($"Imported {counter} countries from I:R.");
		}

		private void ImportImperatorCountry(
			Country country,
			Dependency? dependency,
			CountryCollection imperatorCountries,
			TagTitleMapper tagTitleMapper,
			LocDB irLocDB,
			CK3LocDB ck3LocDB,
			ProvinceMapper provinceMapper,
			CoaMapper coaMapper,
			GovernmentMapper governmentMapper,
			SuccessionLawMapper successionLawMapper,
			DefiniteFormMapper definiteFormMapper,
			ReligionMapper religionMapper,
			CultureMapper cultureMapper,
			NicknameMapper nicknameMapper,
			CharacterCollection characters,
			Date conversionDate,
			Configuration config,
			List<KeyValuePair<Country, Dependency?>> countyLevelCountries,
			IReadOnlyCollection<string> enabledCK3Dlcs) {
			// Create a new title or update existing title.
			var titleId = DetermineId(country, dependency, imperatorCountries, tagTitleMapper, irLocDB, ck3LocDB);

			if (GetRankForId(titleId) == TitleRank.county) {
				countyLevelCountries.Add(new(country, dependency));
				Logger.Debug($"Country {country.Id} can only be converted as county level.");
				return;
			}

			if (TryGetValue(titleId, out var existingTitle)) {
				existingTitle.InitializeFromTag(
					country,
					dependency,
					imperatorCountries,
					irLocDB,
					ck3LocDB,
					provinceMapper,
					coaMapper,
					governmentMapper,
					successionLawMapper,
					definiteFormMapper,
					religionMapper,
					cultureMapper,
					nicknameMapper,
					characters,
					conversionDate,
					config,
					enabledCK3Dlcs
				);
			} else {
				Add(
					country,
					dependency,
					imperatorCountries,
					irLocDB,
					ck3LocDB,
					provinceMapper,
					coaMapper,
					tagTitleMapper,
					governmentMapper,
					successionLawMapper,
					definiteFormMapper,
					religionMapper,
					cultureMapper,
					nicknameMapper,
					characters,
					conversionDate,
					config,
					enabledCK3Dlcs
				);
			}
		}

		internal void ImportImperatorGovernorships(
			Imperator.World irWorld,
			ProvinceCollection ck3Provinces,
			TagTitleMapper tagTitleMapper,
			LocDB irLocDB,
			CK3LocDB ck3LocDB,
			Configuration config,
			ProvinceMapper provinceMapper,
			DefiniteFormMapper definiteFormMapper,
			ImperatorRegionMapper imperatorRegionMapper,
			CoaMapper coaMapper,
			List<Governorship> countyLevelGovernorships
		) {
			Logger.Info("Importing Imperator Governorships...");

			var governorships = irWorld.JobsDB.Governorships;
			var governorshipsPerRegion = governorships.GroupBy(g => g.Region.Id)
				.ToFrozenDictionary(g => g.Key, g => g.Count());

			// landedTitles holds all titles imported from CK3. We'll now overwrite some and
			// add new ones from Imperator governorships.
			var counter = 0;
			foreach (var governorship in governorships) {
				ImportImperatorGovernorship(
					governorship,
					this,
					ck3Provinces,
					irWorld.Provinces,
					irWorld.Characters,
					governorshipsPerRegion[governorship.Region.Id] > 1,
					tagTitleMapper,
					irLocDB,
					ck3LocDB,
					provinceMapper,
					definiteFormMapper,
					imperatorRegionMapper,
					coaMapper,
					countyLevelGovernorships,
					config
				);
				++counter;
			}
			Logger.Info($"Imported {counter} governorships from I:R.");
			Logger.IncrementProgress();
		}
		private void ImportImperatorGovernorship(
			Governorship governorship,
			LandedTitles titles,
			ProvinceCollection ck3Provinces,
			Imperator.Provinces.ProvinceCollection irProvinces,
			Imperator.Characters.CharacterCollection imperatorCharacters,
			bool regionHasMultipleGovernorships,
			TagTitleMapper tagTitleMapper,
			LocDB irLocDB,
			CK3LocDB ck3LocDB,
			ProvinceMapper provinceMapper,
			DefiniteFormMapper definiteFormMapper,
			ImperatorRegionMapper imperatorRegionMapper,
			CoaMapper coaMapper,
			List<Governorship> countyLevelGovernorships,
			Configuration config
		) {
			var country = governorship.Country;

			var id = DetermineId(governorship, titles, irProvinces, ck3Provinces, imperatorRegionMapper, tagTitleMapper, provinceMapper);
			if (id is null) {
				Logger.Warn($"Cannot convert {governorship.Region.Id} of country {country.Id}");
				return;
			}

			if (GetRankForId(id) == TitleRank.county) {
				countyLevelGovernorships.Add(governorship);
				return;
			}

			// Create a new title or update existing title
			if (TryGetValue(id, out var existingTitle)) {
				existingTitle.InitializeFromGovernorship(
					governorship,
					country,
					irProvinces,
					imperatorCharacters,
					regionHasMultipleGovernorships,
					irLocDB,
					ck3LocDB,
					provinceMapper,
					definiteFormMapper,
					imperatorRegionMapper,
					config
				);
			} else {
				Add(
					id,
					governorship,
					country,
					irProvinces,
					imperatorCharacters,
					regionHasMultipleGovernorships,
					irLocDB,
					ck3LocDB,
					provinceMapper,
					coaMapper,
					definiteFormMapper,
					imperatorRegionMapper,
					config
				);
			}
		}

		public void ImportImperatorHoldings(ProvinceCollection ck3Provinces, Imperator.Characters.CharacterCollection irCharacters, Date conversionDate) {
			Logger.Info("Importing Imperator holdings...");
			var counter = 0;
			
			var highLevelTitlesThatHaveHolders = this
				.Where(t => t.Rank >= TitleRank.duchy && t.GetHolderId(conversionDate) != "0")
				.ToImmutableList();
			var highLevelTitleCapitalBaronyIds = highLevelTitlesThatHaveHolders
				.Select(t=>t.CapitalCounty?.CapitalBaronyId ?? t.CapitalBaronyId)
				.ToImmutableHashSet();
			
			// Dukes and above should be excluded from having their holdings converted.
			// Otherwise, governors with holdings would own parts of other governorships.
			var dukeAndAboveIds = highLevelTitlesThatHaveHolders
				.Where(t => t.Rank >= TitleRank.duchy)
				.Select(t => t.GetHolderId(conversionDate))
				.ToImmutableHashSet();
			
			// We exclude baronies that are capitals of duchies and above.
			var eligibleBaronies = this
				.Where(t => t.Rank == TitleRank.barony)
				.Where(b => !highLevelTitleCapitalBaronyIds.Contains(b.Id))
				.ToArray();
			
			var countyCapitalBaronies = eligibleBaronies
				.Where(b => b.DeJureLiege?.CapitalBaronyId == b.Id)
				.OrderBy(b => b.Id)
				.ToArray();
			
			var nonCapitalBaronies = eligibleBaronies.Except(countyCapitalBaronies).OrderBy(b => b.Id).ToArray();
			

			// In CK3, a county holder shouldn't own baronies in counties that are not their own.
			// This dictionary tracks what counties are held by what characters.
			Dictionary<string, HashSet<string>> countiesPerCharacter = []; // characterId -> countyIds
			
			// Evaluate all capital baronies first (we want to distribute counties first, then baronies).
			foreach (var barony in countyCapitalBaronies) {
				var ck3Province = GetBaronyProvince(barony);
				if (ck3Province is null) {
					continue;
				}

				// Skip none holdings and temple holdings.
				if (ck3Province.GetHoldingType(conversionDate) is "church_holding" or "none") {
					continue;
				}

				var irProvince = ck3Province.PrimaryImperatorProvince; // TODO: when the holding owner of the primary I:R province is not able to hold the CK3 equivalent, also check the holding owners from secondary source provinces
				var ck3Owner = GetEligibleCK3OwnerForImperatorProvince(irProvince);
				if (ck3Owner is null) {
					continue;
				}
				
				var realm = ck3Owner.ImperatorCharacter?.HomeCountry?.CK3Title;
				var deFactoLiege = realm;
				if (realm is not null) {
					var deJureDuchy = barony.DeJureLiege?.DeJureLiege;
					if (deJureDuchy is not null && deJureDuchy.GetHolderId(conversionDate) != "0" && deJureDuchy.GetTopRealm(conversionDate) == realm) {
						deFactoLiege = deJureDuchy;
					} else {
						var deJureKingdom = deJureDuchy?.DeJureLiege;
						if (deJureKingdom is not null && deJureKingdom.GetHolderId(conversionDate) != "0" && deJureKingdom.GetTopRealm(conversionDate) == realm) {
							deFactoLiege = deJureKingdom;
						}
					}
				}
				
				// Barony is a county capital, so set the county holder to the holding owner.
				var county = barony.DeJureLiege;
				if (county is null) {
					Logger.Warn($"County capital barony {barony.Id} has no de jure county!");
					continue;
				}
				county.SetHolder(ck3Owner, conversionDate);
				county.SetDeFactoLiege(deFactoLiege, conversionDate);
				
				if (!countiesPerCharacter.TryGetValue(ck3Owner.Id, out var countyIds)) {
					countyIds = [];
					countiesPerCharacter[ck3Owner.Id] = countyIds;
				}
				countyIds.Add(county.Id);
				
				++counter;
			}
			
			// In CK3, a baron that doesn't own counties can only hold a single barony.
			// This dictionary IDs of such barons that already hold a barony.
			HashSet<string> baronyHolderIds = [];
			
			// After all possible county capital baronies are distributed, distribute the rest of the eligible baronies.
			foreach (var barony in nonCapitalBaronies) {
				var ck3Province = GetBaronyProvince(barony);
				if (ck3Province is null) {
					continue;
				}

				// Skip none holdings and temple holdings.
				if (ck3Province.GetHoldingType(conversionDate) is "church_holding" or "none") {
					continue;
				}

				var irProvince = ck3Province.PrimaryImperatorProvince; // TODO: when the holding owner of the primary I:R province is not able to hold the CK3 equivalent, also check the holding owners from secondary source provinces
				var ck3Owner = GetEligibleCK3OwnerForImperatorProvince(irProvince);
				if (ck3Owner is null) {
					continue;
				}
				if (baronyHolderIds.Contains(ck3Owner.Id)) {
					continue;
				}
				
				var county = barony.DeJureLiege;
				if (county is null) {
					Logger.Warn($"Barony {barony.Id} has no de jure county!");
					continue;
				}
				// A non-capital barony cannot be held by a character that owns a county but not the county the barony is in.
				if (countiesPerCharacter.TryGetValue(ck3Owner.Id, out var countyIds) && !countyIds.Contains(county.Id)) {
					continue;
				}
					
				barony.SetHolder(ck3Owner, conversionDate);
				// No need to set de facto liege for baronies, they are tied to counties.
				
				baronyHolderIds.Add(ck3Owner.Id);
				
				++counter;
			}
			Logger.Info($"Imported {counter} holdings from I:R.");
			Logger.IncrementProgress();
			return;

			Province? GetBaronyProvince(Title barony) {
				var ck3ProvinceId = barony.ProvinceId;
				if (ck3ProvinceId is null) {
					return null;
				}
				if (!ck3Provinces.TryGetValue(ck3ProvinceId.Value, out var ck3Province)) {
					return null;
				}
				return ck3Province;
			}

			Character? GetEligibleCK3OwnerForImperatorProvince(Imperator.Provinces.Province? irProvince) {
				var holdingOwnerId = irProvince?.HoldingOwnerId;
				if (holdingOwnerId is null) {
					return null;
				}

				var irOwner = irCharacters[holdingOwnerId.Value];
				var ck3Owner = irOwner.CK3Character;
				if (ck3Owner is null) {
					return null;
				}
				if (dukeAndAboveIds.Contains(ck3Owner.Id)) {
					return null;
				}
				
				return ck3Owner;
			}
		}

		public void RemoveInvalidLandlessTitles(Date ck3BookmarkDate) {
			Logger.Info("Removing invalid landless titles...");
			var removedGeneratedTitles = new HashSet<string>();
			var revokedVanillaTitles = new HashSet<string>();

			HashSet<string> countyHoldersCache = GetCountyHolderIds(ck3BookmarkDate);

			foreach (var title in this) {
				// If duchy/kingdom/empire title holder holds no counties, revoke the title.
				// In case of titles created from Imperator, completely remove them.
				if (title.Rank <= TitleRank.county) {
					continue;
				}
				if (countyHoldersCache.Contains(title.GetHolderId(ck3BookmarkDate))) {
					continue;
				}

				// Check if the title has "landless = yes" attribute.
				// If it does, it should be always kept.
				var id = title.Id;
				if (this[id].Landless) {
					continue;
				}

				if (title.IsCreatedFromImperator) {
					removedGeneratedTitles.Add(id);
					Remove(id);
				} else {
					revokedVanillaTitles.Add(id);
					title.ClearHolderSpecificHistory();
					title.SetDeFactoLiege(newLiege: null, ck3BookmarkDate);
				}
			}
			if (removedGeneratedTitles.Count > 0) {
				Logger.Debug($"Found landless generated titles that can't be landless: {string.Join(", ", removedGeneratedTitles)}");
			}
			if (revokedVanillaTitles.Count > 0) {
				Logger.Debug($"Found landless vanilla titles that can't be landless: {string.Join(", ", revokedVanillaTitles)}");
			}

			Logger.IncrementProgress();
		}

		private void SetDeJureKingdoms(CK3LocDB ck3LocDB, Date ck3BookmarkDate) {
			Logger.Info("Setting de jure kingdoms...");

			var duchies = this.Where(t => t.Rank == TitleRank.duchy).ToFrozenSet();
			var duchiesWithDeJureVassals = duchies.Where(d => d.DeJureVassals.Count > 0).ToFrozenSet();

			foreach (var duchy in duchiesWithDeJureVassals) {
				// If capital county belongs to an empire and contains the empire's capital,
				// create a kingdom from the duchy and make the empire a de jure liege of the kingdom.
				var capitalEmpireRealm = duchy.CapitalCounty?.GetRealmOfRank(TitleRank.empire, ck3BookmarkDate);
				var duchyCounties = duchy.GetDeJureVassalsAndBelow("c").Values;
				if (capitalEmpireRealm is not null && duchyCounties.Any(c => c.Id == capitalEmpireRealm.CapitalCountyId)) {
					var kingdom = Add("k_IRTOCK3_kingdom_from_" + duchy.Id);
					kingdom.Color1 = duchy.Color1;
					kingdom.CapitalCounty = duchy.CapitalCounty;

					var kingdomNameLoc = ck3LocDB.GetOrCreateLocBlock(kingdom.Id);
					kingdomNameLoc.ModifyForEveryLanguage(
						(orig, language) => $"${duchy.Id}$"
					);
					
					var kingdomAdjLoc = ck3LocDB.GetOrCreateLocBlock(kingdom.Id + "_adj");
					string duchyAdjLocKey = duchy.Id + "_adj";
					kingdomAdjLoc.ModifyForEveryLanguage(
						(orig, language) => {
							if (ck3LocDB.HasKeyLocForLanguage(duchyAdjLocKey, language)) {
								return $"${duchyAdjLocKey}$";
							}
							
							Logger.Debug($"Using duchy name as adjective for {kingdom.Id} in {language} because duchy adjective is missing.");
							return $"${duchy.Id}$";
						}
					);
					
					kingdom.DeJureLiege = capitalEmpireRealm;
					duchy.DeJureLiege = kingdom;
					continue;
				}
				
				// If capital county belongs to a kingdom, make the kingdom a de jure liege of the duchy.
				var capitalKingdomRealm = duchy.CapitalCounty?.GetRealmOfRank(TitleRank.kingdom, ck3BookmarkDate);
				if (capitalKingdomRealm is not null) {
					duchy.DeJureLiege = capitalKingdomRealm;
					continue;
				}

				// Otherwise, use the kingdom that owns the biggest percentage of the duchy.
				var kingdomRealmShares = new Dictionary<string, int>(); // realm, number of provinces held in duchy
				foreach (var county in duchyCounties) {
					var kingdomRealm = county.GetRealmOfRank(TitleRank.kingdom, ck3BookmarkDate);
					if (kingdomRealm is null) {
						continue;
					}
					kingdomRealmShares.TryGetValue(kingdomRealm.Id, out int currentCount);
					kingdomRealmShares[kingdomRealm.Id] = currentCount + county.CountyProvinceIds.Count();
				}

				if (kingdomRealmShares.Count > 0) {
					var biggestShare = kingdomRealmShares.MaxBy(pair => pair.Value);
					duchy.DeJureLiege = this[biggestShare.Key];
				}
			}

			// Duchies without de jure vassals should not be de jure part of any kingdom.
			var duchiesWithoutDeJureVassals = duchies.Except(duchiesWithDeJureVassals);
			foreach (var duchy in duchiesWithoutDeJureVassals) {
				Logger.Debug($"Duchy {duchy.Id} has no de jure vassals. Removing de jure liege.");
				duchy.DeJureLiege = null;
			}

			Logger.IncrementProgress();
		}

		private void SetDeJureEmpires(CultureCollection ck3Cultures, CharacterCollection ck3Characters, MapData ck3MapData, CK3LocDB ck3LocDB, Date ck3BookmarkDate) {
			Logger.Info("Setting de jure empires...");
			var deJureKingdoms = GetDeJureKingdoms();
			
			// Try to assign kingdoms to existing empires.
			foreach (var kingdom in deJureKingdoms) {
				var empireShares = new Dictionary<string, int>();
				var kingdomProvincesCount = 0;
				foreach (var county in kingdom.GetDeJureVassalsAndBelow("c").Values) {
					var countyProvincesCount = county.CountyProvinceIds.Count();
					kingdomProvincesCount += countyProvincesCount;

					var empireRealm = county.GetRealmOfRank(TitleRank.empire, ck3BookmarkDate);
					if (empireRealm is null) {
						continue;
					}

					empireShares.TryGetValue(empireRealm.Id, out var currentCount);
					empireShares[empireRealm.Id] = currentCount + countyProvincesCount;
				}

				kingdom.DeJureLiege = null;
				if (empireShares.Count == 0) {
					continue;
				}

				(string empireId, int share) = empireShares.MaxBy(pair => pair.Value);
				// The potential de jure empire must hold at least 50% of the kingdom.
				if (share < (kingdomProvincesCount * 0.50)) {
					continue;
				}

				kingdom.DeJureLiege = this[empireId];
			}

			// For kingdoms that still have no de jure empire, create empires based on dominant culture of the realms
			// holding land in that de jure kingdom.
			var removableEmpireIds = new HashSet<string>();
			var kingdomToDominantHeritagesDict = new Dictionary<string, ImmutableArray<Pillar>>();
			var heritageToEmpireDict = GetHeritageIdToExistingTitleDict();
			CreateEmpiresBasedOnDominantHeritages(deJureKingdoms, ck3Cultures, ck3Characters, removableEmpireIds, kingdomToDominantHeritagesDict, heritageToEmpireDict, ck3LocDB, ck3BookmarkDate);
			
			Logger.Debug("Building kingdom adjacencies dict...");
			// Create a cache of province IDs per kingdom.
			var provincesPerKingdomDict = deJureKingdoms
				.ToFrozenDictionary(
					k => k.Id,
					k => k.GetDeJureVassalsAndBelow("c").Values.SelectMany(c => c.CountyProvinceIds).ToFrozenSet()
				);
			var kingdomAdjacenciesByLand = deJureKingdoms.ToFrozenDictionary(k => k.Id, _ => new ConcurrentHashSet<string>());
			var kingdomAdjacenciesByWaterBody = deJureKingdoms.ToFrozenDictionary(k => k.Id, _ => new ConcurrentHashSet<string>());
			Parallel.ForEach(deJureKingdoms, kingdom => {
				FindKingdomsAdjacentToKingdom(ck3MapData, deJureKingdoms, kingdom.Id, provincesPerKingdomDict, kingdomAdjacenciesByLand, kingdomAdjacenciesByWaterBody);
			});
			
			SplitDisconnectedEmpires(kingdomAdjacenciesByLand, kingdomAdjacenciesByWaterBody, removableEmpireIds, kingdomToDominantHeritagesDict, heritageToEmpireDict, ck3LocDB, ck3BookmarkDate);
			
			SetEmpireCapitals(ck3BookmarkDate);
		}

		private void CreateEmpiresBasedOnDominantHeritages(
			IReadOnlyCollection<Title> deJureKingdoms,
			CultureCollection ck3Cultures,
			CharacterCollection ck3Characters,
			HashSet<string> removableEmpireIds,
			Dictionary<string, ImmutableArray<Pillar>> kingdomToDominantHeritagesDict,
			Dictionary<string, Title> heritageToEmpireDict,
			CK3LocDB ck3LocDB,
			Date ck3BookmarkDate
		) {
			var kingdomsWithoutEmpire = deJureKingdoms
				.Where(k => k.DeJureLiege is null)
				.ToImmutableArray();

			foreach (var kingdom in kingdomsWithoutEmpire) {
				var counties = kingdom.GetDeJureVassalsAndBelow("c").Values;
				
				// Get list of dominant heritages in the kingdom, in descending order.
				var dominantHeritages = counties
					.Select(c => new { County = c, HolderId = c.GetHolderId(ck3BookmarkDate)})
					.Select(x => new { x.County, Holder = ck3Characters.TryGetValue(x.HolderId, out var holder) ? holder : null})
					.Select(x => new { x.County, CultureId = x.Holder?.GetCultureId(ck3BookmarkDate) })
					.Where(x => x.CultureId is not null)
					.Select(x => new { x.County, Culture = ck3Cultures.TryGetValue(x.CultureId!, out var culture) ? culture : null })
					.Where(x => x.Culture is not null)
					.Select(x => new { x.County, x.Culture!.Heritage })
					.GroupBy(x => x.Heritage)
					.OrderByDescending(g => g.Count())
					.Select(g => g.Key)
					.ToImmutableArray();
				if (dominantHeritages.Length == 0) {
					if (kingdom.GetDeJureVassalsAndBelow("c").Count > 0) {
						Logger.Warn($"Kingdom {kingdom.Id} has no dominant heritage!");
					}
					continue;
				}
				kingdomToDominantHeritagesDict[kingdom.Id] = dominantHeritages;

				var dominantHeritage = dominantHeritages[0];

				if (heritageToEmpireDict.TryGetValue(dominantHeritage.Id, out var empire)) {
					kingdom.DeJureLiege = empire;
				} else {
					// Create new de jure empire based on heritage.
					var heritageEmpire = CreateEmpireForHeritage(dominantHeritage, ck3Cultures, ck3LocDB);
					removableEmpireIds.Add(heritageEmpire.Id);
					
					kingdom.DeJureLiege = heritageEmpire;
					heritageToEmpireDict[dominantHeritage.Id] = heritageEmpire;
				}
			}
		}

		private static void FindKingdomsAdjacentToKingdom(
			MapData ck3MapData,
			ImmutableArray<Title> deJureKingdoms,
			string kingdomId, FrozenDictionary<string, FrozenSet<ulong>> provincesPerKingdomDict,
			FrozenDictionary<string, ConcurrentHashSet<string>> kingdomAdjacenciesByLand,
			FrozenDictionary<string, ConcurrentHashSet<string>> kingdomAdjacenciesByWaterBody)
		{
			foreach (var otherKingdom in deJureKingdoms) {
				// Since this code is parallelized, make sure we don't check the same pair twice.
				// Also make sure we don't check the same kingdom against itself.
				if (kingdomId.CompareTo(otherKingdom.Id) >= 0) {
					continue;
				}
				
				var kingdom1Provinces = provincesPerKingdomDict[kingdomId];
				var kingdom2Provinces = provincesPerKingdomDict[otherKingdom.Id];
				if (AreTitlesAdjacentByLand(kingdom1Provinces, kingdom2Provinces, ck3MapData)) {
					kingdomAdjacenciesByLand[kingdomId].Add(otherKingdom.Id);
					kingdomAdjacenciesByLand[otherKingdom.Id].Add(kingdomId);
				} else if (AreTitlesAdjacentByWaterBody(kingdom1Provinces, kingdom2Provinces, ck3MapData)) {
					kingdomAdjacenciesByWaterBody[kingdomId].Add(otherKingdom.Id);
					kingdomAdjacenciesByWaterBody[otherKingdom.Id].Add(kingdomId);
				}
			}
		}

		private Dictionary<string, Title> GetHeritageIdToExistingTitleDict() {
			var heritageToEmpireDict = new Dictionary<string, Title>();

			var reader = new BufferedReader(File.ReadAllText("configurables/heritage_empires_map.txt"));
			foreach (var (heritageId, empireId) in reader.GetAssignments()) {
				if (heritageToEmpireDict.ContainsKey(heritageId)) {
					continue;
				}
				if (!TryGetValue(empireId, out var empire)) {
					continue;
				}
				if (empire.Rank != TitleRank.empire) {
					continue;
				}
				
				heritageToEmpireDict[heritageId] = empire;
				Logger.Debug($"Mapped heritage {heritageId} to empire {empireId}.");
			}
			
			return heritageToEmpireDict;
		}

		private Title CreateEmpireForHeritage(Pillar heritage, CultureCollection ck3Cultures, CK3LocDB ck3LocDB) {
			var newEmpireId = $"e_IRTOCK3_heritage_{heritage.Id}";
			var newEmpire = Add(newEmpireId);
			var nameLocBlock = ck3LocDB.GetOrCreateLocBlock(newEmpire.Id);
			nameLocBlock[ConverterGlobals.PrimaryLanguage] = $"${heritage.Id}_name$ Empire";
			var adjectiveLocBlock = ck3LocDB.GetOrCreateLocBlock($"{newEmpire.Id}_adj");
			adjectiveLocBlock[ConverterGlobals.PrimaryLanguage] = $"${heritage.Id}_name$";
			newEmpire.HasDefiniteForm = true;

			// Use color of one of the cultures as the empire color.
			var empireColor = ck3Cultures.First(c => c.Heritage == heritage).Color;
			newEmpire.Color1 = empireColor;
			
			return newEmpire;
		}

		private void SplitDisconnectedEmpires(
			FrozenDictionary<string, ConcurrentHashSet<string>> kingdomAdjacenciesByLand,
			FrozenDictionary<string, ConcurrentHashSet<string>> kingdomAdjacenciesByWaterBody,
			HashSet<string> removableEmpireIds,
			Dictionary<string, ImmutableArray<Pillar>> kingdomToDominantHeritagesDict,
			Dictionary<string, Title> heritageToEmpireDict,
			CK3LocDB ck3LocDB,
			Date date
		) {
			Logger.Debug("Splitting disconnected empires...");
			
			// Combine kingdom adjacencies by land and water body into a single dictionary.
			var kingdomAdjacencies = new Dictionary<string, HashSet<string>>();
			foreach (var (kingdomId, adjacencies) in kingdomAdjacenciesByLand) {
				kingdomAdjacencies[kingdomId] = [..adjacencies];
			}
			foreach (var (kingdomId, adjacencies) in kingdomAdjacenciesByWaterBody) {
				if (!kingdomAdjacencies.TryGetValue(kingdomId, out var set)) {
					set = [];
					kingdomAdjacencies[kingdomId] = set;
				}
				set.UnionWith(adjacencies);
			}
			
			// If one separated kingdom is separated from the rest of its de jure empire, try to get the second dominant heritage in the kingdom.
			// If any neighboring kingdom has that heritage as dominant one, transfer the separated kingdom to the neighboring kingdom's empire.
			var disconnectedEmpiresDict = GetDictOfDisconnectedEmpires(kingdomAdjacencies, removableEmpireIds);
			if (disconnectedEmpiresDict.Count == 0) {
				return;
			}
			Logger.Debug("\tTransferring stranded kingdoms to neighboring empires...");
			foreach (var (empire, kingdomGroups) in disconnectedEmpiresDict) {
				var dissolvableGroups = kingdomGroups.Where(g => g.Count == 1).ToArray();
				foreach (var group in dissolvableGroups) {
					var kingdom = group.First();
					if (!kingdomToDominantHeritagesDict.TryGetValue(kingdom.Id, out var dominantHeritages)) {
						continue;
					}
					if (dominantHeritages.Length < 2) {
						continue;
					}
					
					var adjacentEmpiresByLand = kingdomAdjacenciesByLand[kingdom.Id].Select(k => this[k].DeJureLiege)
						.Where(e => e is not null)
						.Select(e => e!)
						.ToFrozenSet();
					
					// Try to find valid neighbor by land first, to reduce the number of exclaves.
					Title? validNeighbor = null;
					foreach (var secondaryHeritage in dominantHeritages.Skip(1)) {
						if (!heritageToEmpireDict.TryGetValue(secondaryHeritage.Id, out var heritageEmpire)) {
							continue;
						}
						if (!adjacentEmpiresByLand.Contains(heritageEmpire)) {
							continue;
						}

						validNeighbor = heritageEmpire;
						Logger.Debug($"\t\tTransferring kingdom {kingdom.Id} from empire {empire.Id} to empire {validNeighbor.Id} neighboring by land.");
						break;
					}
					
					// If no valid neighbor by land, try to find valid neighbor by water.
					if (validNeighbor is null) {
						var adjacentEmpiresByWaterBody = kingdomAdjacenciesByWaterBody[kingdom.Id].Select(k => this[k].DeJureLiege)
							.Where(e => e is not null)
							.Select(e => e!)
							.ToFrozenSet();
						
						foreach (var secondaryHeritage in dominantHeritages.Skip(1)) {
							if (!heritageToEmpireDict.TryGetValue(secondaryHeritage.Id, out var heritageEmpire)) {
								continue;
							}
							if (!adjacentEmpiresByWaterBody.Contains(heritageEmpire)) {
								continue;
							}

							validNeighbor = heritageEmpire;
							Logger.Debug($"\t\tTransferring kingdom {kingdom.Id} from empire {empire.Id} to empire {validNeighbor.Id} neighboring by water body.");
							break;
						}
					}

					if (validNeighbor is not null) {
						kingdom.DeJureLiege = validNeighbor;
					}
				}
			}	
			
			disconnectedEmpiresDict = GetDictOfDisconnectedEmpires(kingdomAdjacencies, removableEmpireIds);
			if (disconnectedEmpiresDict.Count == 0) {
				return;
			}
			Logger.Debug("\tCreating new empires for disconnected groups...");
			foreach (var (empire, groups) in disconnectedEmpiresDict) {
				// Keep the largest group as is, and create new empires based on most developed counties for the rest.
				var largestGroup = groups.MaxBy(g => g.Count);
				foreach (var group in groups) {
					if (group == largestGroup) {
						continue;
					}
					
					var mostDevelopedCounty = group
						.SelectMany(k => k.GetDeJureVassalsAndBelow("c").Values)
						.MaxBy(c => c.GetOwnOrInheritedDevelopmentLevel(date));
					if (mostDevelopedCounty is null) {
						continue;
					}
					
					string newEmpireId = $"e_IRTOCK3_from_{mostDevelopedCounty.Id}";
					var newEmpire = Add(newEmpireId);
					newEmpire.Color1 = mostDevelopedCounty.Color1;
					newEmpire.CapitalCounty = mostDevelopedCounty;
					newEmpire.HasDefiniteForm = false;
					
					var empireNameLoc = ck3LocDB.GetOrCreateLocBlock(newEmpireId);
					empireNameLoc.ModifyForEveryLanguage(
						(orig, language) => $"${mostDevelopedCounty.Id}$"
					);
					
					var empireAdjLoc = ck3LocDB.GetOrCreateLocBlock(newEmpireId + "_adj");
					empireAdjLoc.ModifyForEveryLanguage(
						(orig, language) => $"${mostDevelopedCounty.Id}_adj$"
					);

					foreach (var kingdom in group) {
						kingdom.DeJureLiege = newEmpire;
					}
					
					Logger.Debug($"\t\tCreated new empire {newEmpire.Id} for group {string.Join(',', group.Select(k => k.Id))}.");
				}
			}
			
			disconnectedEmpiresDict = GetDictOfDisconnectedEmpires(kingdomAdjacencies, removableEmpireIds);
			if (disconnectedEmpiresDict.Count > 0) {
				Logger.Warn("Failed to split some disconnected empires: " + string.Join(", ", disconnectedEmpiresDict.Keys.Select(e => e.Id)));
			}
		}

		private Dictionary<Title, List<HashSet<Title>>> GetDictOfDisconnectedEmpires(
			Dictionary<string, HashSet<string>> kingdomAdjacencies,
			IReadOnlySet<string> removableEmpireIds
		) {
			var dictToReturn = new Dictionary<Title, List<HashSet<Title>>>();
			
			foreach (var empire in this.Where(t => t.Rank == TitleRank.empire)) {
				IEnumerable<Title> deJureKingdoms = empire.GetDeJureVassalsAndBelow("k").Values;

				// Unassign de jure kingdoms that have no de jure land themselves.
				var deJureKingdomsWithoutLand =
					deJureKingdoms.Where(k => k.GetDeJureVassalsAndBelow("c").Count == 0).ToFrozenSet();
				foreach (var deJureKingdomWithLand in deJureKingdomsWithoutLand) {
					deJureKingdomWithLand.DeJureLiege = null;
				}

				deJureKingdoms = deJureKingdoms.Except(deJureKingdomsWithoutLand).ToArray();

				if (!deJureKingdoms.Any()) {
					if (removableEmpireIds.Contains(empire.Id)) {
						Remove(empire.Id);
					}

					continue;
				}

				// Group the kingdoms into contiguous groups.
				var kingdomGroups = new List<HashSet<Title>>();
				foreach (var kingdom in deJureKingdoms) {
					var added = false;
					List<HashSet<Title>> connectedGroups = [];

					foreach (var group in kingdomGroups) {
						if (group.Any(k => kingdomAdjacencies.TryGetValue(k.Id, out var adjacencies) && adjacencies.Contains(kingdom.Id))) {
							group.Add(kingdom);
							connectedGroups.Add(group);

							added = true;
						}
					}

					// If the kingdom is adjacent to multiple groups, merge them.
					if (connectedGroups.Count > 1) {
						var mergedGroup = new HashSet<Title>();
						foreach (var group in connectedGroups) {
							mergedGroup.UnionWith(group);
							kingdomGroups.Remove(group);
						}

						mergedGroup.Add(kingdom);
						kingdomGroups.Add(mergedGroup);
					}

					if (!added) {
						kingdomGroups.Add([kingdom]);
					}
				}

				if (kingdomGroups.Count <= 1) {
					continue;
				}

				Logger.Debug($"\tEmpire {empire.Id} has {kingdomGroups.Count} disconnected groups of kingdoms: {string.Join(" ; ", kingdomGroups.Select(g => string.Join(',', g.Select(k => k.Id))))}");
				dictToReturn[empire] = kingdomGroups;
			}

			return dictToReturn;
		}

		private static bool AreTitlesAdjacent(FrozenSet<ulong> title1ProvinceIds, FrozenSet<ulong> title2ProvinceIds, MapData mapData) {
			return mapData.AreProvinceGroupsAdjacent(title1ProvinceIds, title2ProvinceIds);
		}
		private static bool AreTitlesAdjacentByLand(FrozenSet<ulong> title1ProvinceIds, FrozenSet<ulong> title2ProvinceIds, MapData mapData) {
			return mapData.AreProvinceGroupsAdjacentByLand(title1ProvinceIds, title2ProvinceIds);
		}
		private static bool AreTitlesAdjacentByWaterBody(FrozenSet<ulong> title1ProvinceIds, FrozenSet<ulong> title2ProvinceIds, MapData mapData) {
			return mapData.AreProvinceGroupsConnectedByWaterBody(title1ProvinceIds, title2ProvinceIds);
		}

		private void SetEmpireCapitals(Date ck3BookmarkDate) {
			// Make sure every empire's capital is within the empire's de jure land.
			Logger.Info("Setting empire capitals...");
			foreach (var empire in this.Where(t => t.Rank == TitleRank.empire)) {
				var deJureCounties = empire.GetDeJureVassalsAndBelow("c").Values;
				
				// If the empire already has a set capital, and it's within the de jure land, keep it.
				if (empire.CapitalCounty is not null && deJureCounties.Contains(empire.CapitalCounty)) {
					continue;
				}
				
				// Try to use most developed county among the de jure kingdom capitals.
				var deJureKingdoms = empire.GetDeJureVassalsAndBelow("k").Values;
				var mostDevelopedCounty = deJureKingdoms
					.Select(k => k.CapitalCounty)
					.Where(c => c is not null)
					.MaxBy(c => c!.GetOwnOrInheritedDevelopmentLevel(ck3BookmarkDate));
				if (mostDevelopedCounty is not null) {
					empire.CapitalCounty = mostDevelopedCounty;
					continue;
				}
				
				// Otherwise, use the most developed county among the de jure empire's counties.
				mostDevelopedCounty = deJureCounties
					.MaxBy(c => c.GetOwnOrInheritedDevelopmentLevel(ck3BookmarkDate));
				if (mostDevelopedCounty is not null) {
					empire.CapitalCounty = mostDevelopedCounty;
				}
			}
		}

		public void SetDeJureKingdomsAndEmpires(Date ck3BookmarkDate, CultureCollection ck3Cultures, CharacterCollection ck3Characters, MapData ck3MapData, CK3LocDB ck3LocDB) {
			SetDeJureKingdoms(ck3LocDB, ck3BookmarkDate);
			SetDeJureEmpires(ck3Cultures, ck3Characters, ck3MapData, ck3LocDB, ck3BookmarkDate);
		}

		private HashSet<string> GetCountyHolderIds(Date date) {
			var countyHoldersCache = new HashSet<string>();
			foreach (var county in this.Where(t => t.Rank == TitleRank.county)) {
				var holderId = county.GetHolderId(date);
				if (holderId != "0") {
					countyHoldersCache.Add(holderId);
				}
			}

			return countyHoldersCache;
		}

		public void ImportDevelopmentFromImperator(ProvinceCollection ck3Provinces, Date date, double irCivilizationWorth) {
			static bool IsCountyOutsideImperatorMap(Title county, IReadOnlyDictionary<string, int> irProvsPerCounty) {
				return irProvsPerCounty[county.Id] == 0;
			}

			double CalculateCountyDevelopment(Title county) {
				double dev = 0;
				IEnumerable<ulong> countyProvinceIds = county.CountyProvinceIds;
				int provsCount = 0;
				foreach (var ck3ProvId in countyProvinceIds) {
					if (!ck3Provinces.TryGetValue(ck3ProvId, out var ck3Province)) {
						Logger.Warn($"CK3 province {ck3ProvId} not found!");
						continue;
					}
					var sourceProvinces = ck3Province.ImperatorProvinces;
					if (sourceProvinces.Count == 0) {
						continue;
					}
					++provsCount;
					
					var devFromProvince = sourceProvinces.Average(srcProv => srcProv.CivilizationValue);
					dev += devFromProvince;
				}

				dev = Math.Max(0, dev - Math.Sqrt(dev));
				if (provsCount > 0) {
					dev /= provsCount;
				}
				dev *= irCivilizationWorth;
				return dev;
			}

			Logger.Info("Importing development from Imperator...");

			var counties = this.Where(t => t.Rank == TitleRank.county).ToArray();
			var irProvsPerCounty = GetIRProvsPerCounty(ck3Provinces, counties);

			foreach (var county in counties) {
				if (IsCountyOutsideImperatorMap(county, irProvsPerCounty)) {
					// Don't change development for counties outside of Imperator map.
					continue;
				}

				double dev = CalculateCountyDevelopment(county);

				county.History.Fields.Remove("development_level");
				county.History.AddFieldValue(date, "development_level", "change_development_level", (int)dev);
			}
			
			DistributeExcessDevelopment(date);

			Logger.IncrementProgress();
			return;

			static Dictionary<string, int> GetIRProvsPerCounty(ProvinceCollection ck3Provinces, IEnumerable<Title> counties) {
				Dictionary<string, int> irProvsPerCounty = [];
				foreach (var county in counties) {
					HashSet<ulong> imperatorProvs = [];
					foreach (ulong ck3ProvId in county.CountyProvinceIds) {
						if (!ck3Provinces.TryGetValue(ck3ProvId, out var ck3Province)) {
							Logger.Warn($"CK3 province {ck3ProvId} not found!");
							continue;
						}

						var sourceProvinces = ck3Province.ImperatorProvinces;
						foreach (var irProvince in sourceProvinces) {
							imperatorProvs.Add(irProvince.Id);
						}
					}

					irProvsPerCounty[county.Id] = imperatorProvs.Count;
				}

				return irProvsPerCounty;
			}
		}

		private void DistributeExcessDevelopment(Date date) {
			var topRealms = this
				.Where(t => t.Rank > TitleRank.county && t.GetHolderId(date) != "0" && t.GetDeFactoLiege(date) is null)
				.ToArray();

			// For every realm, get list of counties with over 100 development.
			// Distribute the excess development to the realm's least developed counties.
			foreach (var realm in topRealms) {
				var realmCounties = realm.GetDeFactoVassalsAndBelow(date, "c").Values
					.Select(c => new { County = c, Development = c.GetOwnOrInheritedDevelopmentLevel(date) })
					.Where(c => c.Development.HasValue)
					.Select(c => new { c.County, Development = c.Development!.Value })
					.ToArray();
				var excessDevCounties = realmCounties
					.Where(c => c.Development > 100)
					.OrderByDescending(c => c.Development)
					.ToArray();
				if (excessDevCounties.Length == 0) {
					continue;
				}

				var leastDevCounties = realmCounties
					.Where(c => c.Development < 100)
					.OrderBy(c => c.Development)
					.Select(c => c.County)
					.ToList();
				if (leastDevCounties.Count == 0) {
					continue;
				}
				
				var excessDevSum = excessDevCounties.Sum(c => c.Development - 100);
				Logger.Debug($"Top realm {realm.Id} has {excessDevSum} excess development to distribute among {leastDevCounties.Count} counties.");

				// Now that we've calculated the excess dev, we can cap the county dev at 100.
				foreach (var excessDevCounty in excessDevCounties) {
					excessDevCounty.County.SetDevelopmentLevel(100, date);
				}

				while (excessDevSum > 0 && leastDevCounties.Count > 0) {
					var devPerCounty = excessDevSum / leastDevCounties.Count;
					foreach (var county in leastDevCounties.ToArray()) {
						var currentDev = county.GetOwnOrInheritedDevelopmentLevel(date) ?? 0;
						var devToAdd = Math.Max(devPerCounty, 100 - currentDev);
						var newDevValue = currentDev + devToAdd;

						county.SetDevelopmentLevel(newDevValue, date);
						excessDevSum -= devToAdd;
						if (newDevValue >= 100) {
							leastDevCounties.Remove(county);
						}
					}
				}
			}
		}
	
		/// <summary>
		/// Import Imperator officials as council members and courtiers.
		/// https://imperator.paradoxwikis.com/Position
		/// https://ck3.paradoxwikis.com/Council
		/// https://ck3.paradoxwikis.com/Court#Court_positions
		/// </summary>
		public void ImportImperatorGovernmentOffices(ICollection<OfficeJob> irOfficeJobs, ReligionCollection religionCollection, Date irSaveDate) {
			Logger.Info("Converting government offices...");
			var titlesFromImperator = GetCountriesImportedFromImperator();
			
			var councilPositionToSourcesDict = new Dictionary<string, string[]> {
				["councillor_court_chaplain"] = ["office_augur", "office_pontifex", "office_high_priest_monarchy", "office_high_priest", "office_wise_person"],
				["councillor_chancellor"] = ["office_censor", "office_foreign_minister", "office_arbitrator", "office_elder"],
				["councillor_steward"] = ["office_praetor", "office_magistrate", "office_steward", "office_tribune_of_the_treasury"],
				["councillor_marshal"] = ["office_tribune_of_the_soldiers", "office_marshal", "office_master_of_the_guard", "office_warchief", "office_bodyguard"],
				["councillor_spymaster"] = [], // No equivalents found in Imperator.
			};
			
			// Court positions.
			var courtPositionToSourcesDict = new Dictionary<string, string[]> {
				["bodyguard_court_position"] = ["office_master_of_the_guard", "office_bodyguard"],
				["court_physician_court_position"] = ["office_physician", "office_republic_physician", "office_apothecary"],
				["court_tutor_court_position"] = ["office_royal_tutor"],
				["chronicler_court_position"] = ["office_philosopher"], // From I:R wiki: "supervises libraries and the gathering and protection of knowledge"
				["cave_hermit_court_position"] = ["office_wise_person"]
			};

			string[] ignoredOfficeTypes = ["office_plebeian_aedile"];

			// Log all unhandled office types.
			var irOfficeTypesFromSave = irOfficeJobs.Select(j => j.OfficeType).ToFrozenSet();
			var handledOfficeTypes = councilPositionToSourcesDict.Values
				.SelectMany(v => v)
				.Concat(courtPositionToSourcesDict.Values.SelectMany(v => v))
				.Concat(ignoredOfficeTypes)
				.ToFrozenSet();
			var unmappedOfficeTypes = irOfficeTypesFromSave
				.Where(officeType => !handledOfficeTypes.Contains(officeType)).ToArray();
			if (unmappedOfficeTypes.Length > 0) {
				Logger.Error($"Unmapped office types: {string.Join(", ", unmappedOfficeTypes)}");
			}

			foreach (var title in titlesFromImperator) {
				var country = title.ImperatorCountry!;
				var ck3Ruler = country.Monarch?.CK3Character;
				if (ck3Ruler is null) {
					continue;
				}
				
				// Make sure the ruler actually holds something in CK3.
				if (this.All(t => t.GetHolderId(irSaveDate) != ck3Ruler.Id)) {
					continue;
				}
				
				var convertibleJobs = irOfficeJobs.Where(j => j.CountryId == country.Id).ToList();
				if (convertibleJobs.Count == 0) {
					continue;
				}
				
				var alreadyEmployedCharacters = new HashSet<string>();
				title.AppointCouncilMembersFromImperator(religionCollection, councilPositionToSourcesDict, convertibleJobs, alreadyEmployedCharacters, ck3Ruler, irSaveDate);
				title.AppointCourtierPositionsFromImperator(courtPositionToSourcesDict, convertibleJobs, alreadyEmployedCharacters, ck3Ruler, irSaveDate);
			}
		}

		public IEnumerable<Title> GetCountriesImportedFromImperator() {
			return this.Where(t => t.ImperatorCountry is not null);
		}

		public IReadOnlyCollection<Title> GetDeJureDuchies() => this
			.Where(t => t is {Rank: TitleRank.duchy, DeJureVassals.Count: > 0})
			.ToImmutableArray();
		
		public ImmutableArray<Title> GetDeJureKingdoms() => this
			.Where(t => t is {Rank: TitleRank.kingdom, DeJureVassals.Count: > 0})
			.ToImmutableArray();
		
		private FrozenSet<Color> UsedColors => this.Select(t => t.Color1).Where(c => c is not null).ToFrozenSet()!;
		public bool IsColorUsed(Color color) {
			return UsedColors.Contains(color);
		}
		public Color GetDerivedColor(Color baseColor) {
			FrozenSet<Color> usedHueColors = UsedColors.Where(c => Math.Abs(c.H - baseColor.H) < 0.001).ToFrozenSet();

			for (double v = 0.05; v <= 1; v += 0.02) {
				var newColor = new Color(baseColor.H, baseColor.S, v);
				if (usedHueColors.Contains(newColor)) {
					continue;
				}
				return newColor;
			}

			Logger.Warn($"Couldn't generate new color from base {baseColor.OutputRgb()}");
			return baseColor;
		}

		private readonly HistoryFactory titleHistoryFactory = new HistoryFactory.HistoryFactoryBuilder()
			.WithSimpleField("holder", new OrderedSet<string> { "holder", "holder_ignore_head_of_faith_requirement" }, initialValue: null)
			.WithSimpleField("government", "government", initialValue: null)
			.WithSimpleField("liege", "liege", initialValue: null)
			.WithSimpleField("development_level", "change_development_level", initialValue: null)
			.WithSimpleField("succession_laws", "succession_laws", new SortedSet<string>())
			.Build();

		public void LoadHistory(Configuration config, ModFilesystem ck3ModFS) {
			var ck3BookmarkDate = config.CK3BookmarkDate;

			int loadedHistoriesCount = 0;

			var titlesHistoryParser = new Parser();
			titlesHistoryParser.RegisterRegex(Regexes.TitleId, (reader, titleName) => {
				var historyItem = reader.GetStringOfItem().ToString();
				if (!historyItem.Contains('{')) {
					return;
				}

				if (!TryGetValue(titleName, out var title)) {
					return;
				}

				var tempReader = new BufferedReader(historyItem);

				titleHistoryFactory.UpdateHistory(title.History, tempReader);
				++loadedHistoriesCount;
			});
			titlesHistoryParser.RegisterRegex(CommonRegexes.Catchall, ParserHelpers.IgnoreAndLogItem);

			Logger.Info("Parsing title history...");
			titlesHistoryParser.ParseGameFolder("history/titles", ck3ModFS, "txt", recursive: true, logFilePaths: true);
			Logger.Info($"Loaded {loadedHistoriesCount} title histories.");

			// Add vanilla development to counties
			// For counties that inherit development level from de jure lieges, assign it to them directly for better reliability.
			foreach (var title in this.Where(t => t.Rank == TitleRank.county && t.GetDevelopmentLevel(ck3BookmarkDate) is null)) {
				var inheritedDev = title.GetOwnOrInheritedDevelopmentLevel(ck3BookmarkDate);
				title.SetDevelopmentLevel(inheritedDev ?? 0, ck3BookmarkDate);
			}

			// Remove history entries past the bookmark date.
			foreach (var title in this) {
				title.RemoveHistoryPastDate(ck3BookmarkDate);
			}
		}

		public void LoadCulturalNamesFromConfigurables() {
			const string filePath = "configurables/cultural_title_names.txt";
			Logger.Info($"Loading cultural title names from \"{filePath}\"...");

			var parser = new Parser();
			parser.RegisterRegex(CommonRegexes.String, (reader, titleId) => {
				var nameListToLocKeyDict = reader.GetAssignmentsAsDict();

				if (!TryGetValue(titleId, out var title)) {
					return;
				}
				if (title.CulturalNames is null) {
					title.CulturalNames = nameListToLocKeyDict;
				} else {
					foreach (var (nameList, locKey) in nameListToLocKeyDict) {
						title.CulturalNames[nameList] = locKey;
					}
				}
			});
			parser.IgnoreAndLogUnregisteredItems();
			parser.ParseFile(filePath);
		}

		internal void SetCoatsOfArms(CoaMapper coaMapper) {
			Logger.Info("Setting coats of arms for CK3 titles...");
			
			int counter = 0;
			foreach (var title in this) {
				var coa = coaMapper.GetCoaForFlagName(title.Id, warnIfMissing: false);
				if (coa is null) {
					continue;
				}
				
				title.CoA = coa;
				++counter;
			}
			
			Logger.Debug($"Set coats of arms for {counter} CK3 titles.");
		}

		public void RemoveLiegeEntriesFromReligiousHeadHistory(ReligionCollection religions) {
			var religiousHeadTitleIds = religions.Faiths
				.Select(f => f.ReligiousHeadTitleId)
				.Distinct()
				.Where(id => id is not null)
				.Select(id => id!);
			foreach (var religiousHeadTitleId in religiousHeadTitleIds) {
				if (!TryGetValue(religiousHeadTitleId, out var religiousHeadTitle)) {
					continue;
				}
				
				religiousHeadTitle.History.Fields.Remove("liege");
			}
		}
	}
}