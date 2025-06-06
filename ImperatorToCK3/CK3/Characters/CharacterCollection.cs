﻿using commonItems;
using commonItems.Collections;
using commonItems.Localization;
using ImperatorToCK3.CK3.Armies;
using ImperatorToCK3.CK3.Cultures;
using ImperatorToCK3.CK3.Dynasties;
using ImperatorToCK3.CK3.Titles;
using ImperatorToCK3.CommonUtils;
using ImperatorToCK3.CommonUtils.Map;
using ImperatorToCK3.Imperator.Armies;
using ImperatorToCK3.Imperator.Characters;
using ImperatorToCK3.Mappers.Culture;
using ImperatorToCK3.Mappers.DeathReason;
using ImperatorToCK3.Mappers.Nickname;
using ImperatorToCK3.Mappers.Province;
using ImperatorToCK3.Mappers.Religion;
using ImperatorToCK3.Mappers.Trait;
using ImperatorToCK3.Mappers.UnitType;
using Open.Collections;
using System;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace ImperatorToCK3.CK3.Characters;

internal sealed partial class CharacterCollection : ConcurrentIdObjectCollection<string, Character> {
	internal void ImportImperatorCharacters(
		Imperator.World impWorld,
		ReligionMapper religionMapper,
		CultureMapper cultureMapper,
		CultureCollection ck3Cultures,
		TraitMapper traitMapper,
		NicknameMapper nicknameMapper,
		ProvinceMapper provinceMapper,
		DeathReasonMapper deathReasonMapper,
		DNAFactory dnaFactory,
		CK3LocDB ck3LocDB,
		Date conversionDate,
		Configuration config
	) {
		Logger.Info("Importing Imperator Characters...");

		var unlocalizedImperatorNames = new ConcurrentHashSet<string>();

		var parallelOptions = new ParallelOptions {
			MaxDegreeOfParallelism = Environment.ProcessorCount - 1,
		};

		Parallel.ForEach(impWorld.Characters, parallelOptions, irCharacter => {
			ImportImperatorCharacter(
				irCharacter,
				religionMapper,
				cultureMapper,
				traitMapper,
				nicknameMapper,
				impWorld.LocDB,
				ck3LocDB,
				impWorld.MapData,
				provinceMapper,
				deathReasonMapper,
				dnaFactory,
				conversionDate,
				config,
				unlocalizedImperatorNames
			);
		});
	
		if (unlocalizedImperatorNames.Any()) {
			Logger.Warn("Found unlocalized Imperator names: " + string.Join(", ", unlocalizedImperatorNames));
		}
		Logger.Info($"Imported {impWorld.Characters.Count} characters.");

		LinkMothersAndFathers();
		LinkSpouses(conversionDate);
		LinkPrisoners(conversionDate);

		ImportFriendships(impWorld.Characters, conversionDate);
		ImportRivalries(impWorld.Characters, conversionDate);
		Logger.IncrementProgress();

		ImportPregnancies(impWorld.Characters, conversionDate);

		if (config.FallenEagleEnabled) {
			SetCharacterCastes(ck3Cultures, config.CK3BookmarkDate);
		}
	}

	private void ImportImperatorCharacter(
		Imperator.Characters.Character irCharacter,
		ReligionMapper religionMapper,
		CultureMapper cultureMapper,
		TraitMapper traitMapper,
		NicknameMapper nicknameMapper,
		LocDB irLocDB,
		CK3LocDB ck3LocDB,
		MapData irMapData,
		ProvinceMapper provinceMapper,
		DeathReasonMapper deathReasonMapper,
		DNAFactory dnaFactory,
		Date endDate,
		Configuration config,
		ISet<string> unlocalizedImperatorNames) {
		// Create a new CK3 character.
		var newCharacter = new Character(
			irCharacter,
			this,
			religionMapper,
			cultureMapper,
			traitMapper,
			nicknameMapper,
			irLocDB,
			ck3LocDB,
			irMapData,
			provinceMapper,
			deathReasonMapper,
			dnaFactory,
			endDate,
			config,
			unlocalizedImperatorNames
		);
		irCharacter.CK3Character = newCharacter;
		AddOrReplace(newCharacter);
	}

	public override void Remove(string key) {
		BulkRemove([key]);
	}

	private void BulkRemove(ICollection<string> keys) {
		foreach (var key in keys) {
			var characterToRemove = this[key];

			characterToRemove.RemoveAllSpouses();
			characterToRemove.RemoveAllConcubines();
			characterToRemove.RemoveAllChildren();

			var irCharacter = characterToRemove.ImperatorCharacter;
			if (irCharacter is not null) {
				irCharacter.CK3Character = null;
			}

			base.Remove(key);
		}

		RemoveCharacterReferencesFromHistory(keys);
	}

	private void RemoveCharacterReferencesFromHistory(ICollection<string> idsToRemove) {
		var idsCapturingGroup = "(" + string.Join('|', idsToRemove) + ")";

		// Effects like "break_alliance = character:ID" entries should be removed.
		const string commandsGroup = "(break_alliance|make_concubine)";
		var simpleCommandsRegex = new Regex(commandsGroup + @"\s*=\s*character:" + idsCapturingGroup + @"\s*\b");

		foreach (var character in this) {
			var effectsHistoryField = character.History.Fields["effects"];
			if (effectsHistoryField is not LiteralHistoryField effectsLiteralField) {
				Logger.Warn($"Effects history field for character {character.Id} is not a literal field!");
				continue;
			}
			if (effectsLiteralField.EntriesCount == 0) {
				continue;
			}

			effectsLiteralField.RegexReplaceAllEntries(simpleCommandsRegex, string.Empty);

			// Remove all empty effect blocks (effect = { }).
			effectsHistoryField.RemoveAllEntries(entryValue => {
				if (entryValue is not string valueString) {
					return false;
				}

				string trimmedBlock = valueString.Trim();
				if (!trimmedBlock.StartsWith('{') || !trimmedBlock.EndsWith('}')) {
					return false;
				}

				return trimmedBlock[1..^1].Trim().Length == 0;
			});
		}
	}

	private void LinkMothersAndFathers() {
		var motherCounter = 0;
		var fatherCounter = 0;
		foreach (var ck3Character in this) {
			// make links between Imperator characters
			if (ck3Character.ImperatorCharacter is null) {
				// imperatorRegnal characters do not have ImperatorCharacter
				continue;
			}
			var irMotherCharacter = ck3Character.ImperatorCharacter.Mother;
			if (irMotherCharacter is not null) {
				var ck3MotherCharacter = irMotherCharacter.CK3Character;
				if (ck3MotherCharacter is not null) {
					ck3Character.Mother = ck3MotherCharacter;
					++motherCounter;
				} else {
					Logger.Warn($"Imperator mother {irMotherCharacter.Id} has no CK3 character!");
				}
			}

			// make links between Imperator characters
			var irFatherCharacter = ck3Character.ImperatorCharacter.Father;
			if (irFatherCharacter is not null) {
				var ck3FatherCharacter = irFatherCharacter.CK3Character;
				if (ck3FatherCharacter is not null) {
					ck3Character.Father = ck3FatherCharacter;
					++fatherCounter;
				} else {
					Logger.Warn($"Imperator father {irFatherCharacter.Id} has no CK3 character!");
				}
			}
		}
		Logger.Info($"{motherCounter} mothers and {fatherCounter} fathers linked in CK3.");
	}

	private void LinkSpouses(Date conversionDate) {
		var spouseCounter = 0;
		foreach (var ck3Character in this) {
			if (ck3Character.Female) {
				continue; // we set spouses for males to avoid doubling marriages
			}
			// make links between Imperator characters
			if (ck3Character.ImperatorCharacter is null) {
				// imperatorRegnal characters do not have ImperatorCharacter
				continue;
			}
			foreach (var impSpouseCharacter in ck3Character.ImperatorCharacter.Spouses.Values) {
				var ck3SpouseCharacter = impSpouseCharacter.CK3Character;
				if (ck3SpouseCharacter is null) {
					Logger.Warn($"Imperator spouse {impSpouseCharacter.Id} has no CK3 character!");
					continue;
				}

				// Imperator saves don't seem to store marriage date
				Date estimatedMarriageDate = GetEstimatedMarriageDate(ck3Character.ImperatorCharacter, impSpouseCharacter);

				ck3Character.AddSpouse(estimatedMarriageDate, ck3SpouseCharacter);
				++spouseCounter;
			}
		}
		Logger.Info($"{spouseCounter} spouses linked in CK3.");

		Date GetEstimatedMarriageDate(Imperator.Characters.Character imperatorCharacter, Imperator.Characters.Character imperatorSpouse) {
			// Imperator saves don't seem to store marriage date.

			var marriageDeathDate = GetMarriageDeathDate(imperatorCharacter, imperatorSpouse);
			var birthDateOfCommonChild = GetBirthDateOfFirstCommonChild(imperatorCharacter, imperatorSpouse);
			if (birthDateOfCommonChild is not null) {
				// We assume the child was conceived after marriage.
				var estimatedConceptionDate = birthDateOfCommonChild.ChangeByDays(-280);
				if (marriageDeathDate is not null && marriageDeathDate < estimatedConceptionDate) {
					estimatedConceptionDate = marriageDeathDate.ChangeByDays(-1);
				}
				return estimatedConceptionDate;
			}

			if (marriageDeathDate is not null) {
				return marriageDeathDate.ChangeByDays(-1); // Death is not a good moment to marry.
			}

			return conversionDate;
		}
		Date? GetBirthDateOfFirstCommonChild(Imperator.Characters.Character father, Imperator.Characters.Character mother) {
			var childrenOfFather = father.Children.Values.ToFrozenSet();
			var childrenOfMother = mother.Children.Values.ToFrozenSet();
			var commonChildren = childrenOfFather.Intersect(childrenOfMother).OrderBy(child => child.BirthDate).ToArray();

			Date? firstChildBirthDate = commonChildren.Length > 0 ? commonChildren.FirstOrDefault()?.BirthDate : null;
			if (firstChildBirthDate is not null) {
				return firstChildBirthDate;
			}

			var unborns = mother.Unborns.Where(u => u.FatherId == father.Id).OrderBy(u => u.BirthDate).ToArray();
			return unborns.FirstOrDefault()?.BirthDate;
		}

		Date? GetMarriageDeathDate(Imperator.Characters.Character husband, Imperator.Characters.Character wife) {
			if (husband.DeathDate is not null && wife.DeathDate is not null) {
				return husband.DeathDate < wife.DeathDate ? husband.DeathDate : wife.DeathDate;
			}
			return husband.DeathDate ?? wife.DeathDate;
		}
	}

	private void LinkPrisoners(Date date) {
		var prisonerCount = this.Count(character => character.LinkJailor(date));
		Logger.Info($"{prisonerCount} prisoners linked with jailors in CK3.");
	}

	private static void ImportFriendships(Imperator.Characters.CharacterCollection irCharacters, Date conversionDate) {
		Logger.Info("Importing friendships...");
		foreach (var irCharacter in irCharacters) {
			var ck3Character = irCharacter.CK3Character;
			if (ck3Character is null) {
				Logger.Warn($"Imperator character {irCharacter.Id} has no CK3 character!");
				continue;
			}

			foreach (var irFriendId in irCharacter.FriendIds) {
				// Make sure to only add this relation once.
				if (irCharacter.Id.CompareTo(irFriendId) > 0) {
					continue;
				}

				var irFriend = irCharacters[irFriendId];
				var ck3Friend = irFriend.CK3Character;

				if (ck3Friend is not null) {
					var effectStr = $"{{ set_relation_friend={{ reason=friend_generic_history target=character:{ck3Friend.Id} }} }}";
					ck3Character.History.AddFieldValue(conversionDate, "effects", "effect", effectStr);
				} else {
					Logger.Warn($"Imperator friend {irFriendId} has no CK3 character!");
				}
			}
		}
	}

	private static void ImportRivalries(Imperator.Characters.CharacterCollection irCharacters, Date conversionDate) {
		Logger.Info("Importing rivalries...");
		foreach (var irCharacter in irCharacters) {
			var ck3Character = irCharacter.CK3Character;
			if (ck3Character is null) {
				Logger.Warn($"Imperator character {irCharacter.Id} has no CK3 character!");
				continue;
			}

			foreach (var irRivalId in irCharacter.RivalIds) {
				// Make sure to only add this relation once.
				if (irCharacter.Id.CompareTo(irRivalId) > 0) {
					continue;
				}

				var irRival = irCharacters[irRivalId];
				var ck3Rival = irRival.CK3Character;

				if (ck3Rival is not null) {
					var effectStr = $"{{ set_relation_rival={{ reason=rival_historical target=character:{ck3Rival.Id} }} }}";
					ck3Character.History.AddFieldValue(conversionDate, "effects", "effect", effectStr);
				} else {
					Logger.Warn($"Imperator rival {irRivalId} has no CK3 character!");
				}
			}
		}
	}
	private void ImportPregnancies(Imperator.Characters.CharacterCollection imperatorCharacters, Date conversionDate) {
		Logger.Info("Importing pregnancies...");
		foreach (var female in this.Where(c => c.Female)) {
			var imperatorFemale = female.ImperatorCharacter;
			if (imperatorFemale is null) {
				continue;
			}

			foreach (var unborn in imperatorFemale.Unborns) {
				var conceptionDate = unborn.EstimatedConceptionDate;

				// in CK3 the make_pregnant effect used in character history is executed on game start, so
				// it only makes sense to convert pregnancies that lasted around 3 months or less
				// (longest recorded pregnancy was around 12 months)
				var pregnancyLength = conversionDate.DiffInYears(conceptionDate);
				if (pregnancyLength > 0.25) {
					continue;
				}

				if (!imperatorCharacters.TryGetValue(unborn.FatherId, out var imperatorFather)) {
					continue;
				}

				var ck3Father = imperatorFather.CK3Character;
				if (ck3Father is null) {
					continue;
				}

				female.Pregnancies.Add(new(ck3Father.Id, female.Id, unborn.BirthDate, unborn.IsBastard));
			}
		}

		Logger.IncrementProgress();
	}

	private void SetCharacterCastes(CultureCollection cultures, Date ck3BookmarkDate) {
		var casteSystemCultureIds = cultures
			.Where(c => c.TraditionIds.Contains("tradition_caste_system"))
			.Select(c => c.Id)
			.ToFrozenSet();
		var learningEducationTraits = new[]{"education_learning_1", "education_learning_2", "education_learning_3", "education_learning_4"};

		foreach (var character in this.OrderBy(c => c.BirthDate)) {
			if (character.ImperatorCharacter is null) {
				continue;
			}

			var cultureId = character.GetCultureId(ck3BookmarkDate);
			if (cultureId is null || !casteSystemCultureIds.Contains(cultureId)) {
				continue;
			}

			// The caste is hereditary.
			var father = character.Father;
			if (father is not null) {
				var foundTrait = GetCasteTraitFromParent(father);
				if (foundTrait is not null) {
					character.AddBaseTrait(foundTrait);
					continue;
				}
			}
			var mother = character.Mother;
			if (mother is not null) {
				var foundTrait = GetCasteTraitFromParent(mother);
				if (foundTrait is not null) {
					character.AddBaseTrait(foundTrait);
					continue;
				}
			}

			// Try to set caste based on character's traits.
			var traitIds = character.BaseTraits.ToFrozenSet();
			character.AddBaseTrait(traitIds.Intersect(learningEducationTraits).Any() ? "brahmin" : "kshatriya");
		}
		return;

		static string? GetCasteTraitFromParent(Character parentCharacter) {
			var casteTraits = new[]{"brahmin", "kshatriya", "vaishya", "shudra"};
			var parentTraitIds = parentCharacter.BaseTraits.ToFrozenSet();
			return casteTraits.Intersect(parentTraitIds).FirstOrDefault();
		}
	}

	public void LoadCharacterIDsToPreserve(Date ck3BookmarkDate) {
		Logger.Debug("Loading IDs of CK3 characters to preserve...");

		const string configurablePath = "configurables/ck3_characters_to_preserve.txt";
		var parser = new Parser();
		parser.RegisterKeyword("keep_as_is", reader => {
			var ids = reader.GetStrings();
			foreach (var id in ids) {
				if (!TryGetValue(id, out var character)) {
					continue;
				}

				character.IsNonRemovable = true;
			}
		});
		parser.RegisterKeyword("after_bookmark_date", reader => {
			var ids = reader.GetStrings();
			foreach (var id in ids) {
				if (!TryGetValue(id, out var character)) {
					continue;
				}

				character.IsNonRemovable = true;
				character.BirthDate = ck3BookmarkDate.ChangeByDays(1);
				character.DeathDate = ck3BookmarkDate.ChangeByDays(2);
				// Remove all dated history entries other than birth and death.
				foreach (var field in character.History.Fields) {
					if (field.Id == "birth" || field.Id == "death") {
						continue;
					}
					field.DateToEntriesDict.Clear();
				}
			}
		});
		parser.IgnoreAndLogUnregisteredItems();
		parser.ParseFile(configurablePath);
	}

	public void PurgeUnneededCharacters(Title.LandedTitles titles, DynastyCollection dynasties, HouseCollection houses, Date ck3BookmarkDate) {
		Logger.Info("Purging unneeded characters...");
		
		// Characters from CK3 that hold titles at the bookmark date should be kept.
		var currentTitleHolderIds = titles.GetHolderIdsForAllTitlesExceptNobleFamilyTitles(ck3BookmarkDate);
		var landedCharacters = this
			.Where(character => currentTitleHolderIds.Contains(character.Id))
			.ToArray();
		var charactersToCheck = this.Except(landedCharacters);
		
		// Characters from I:R that held or hold titles should be kept.
		var allTitleHolderIds = titles.GetAllHolderIds();
		var imperatorTitleHolders = this
			.Where(character => character.FromImperator && allTitleHolderIds.Contains(character.Id))
			.ToArray();
		charactersToCheck = charactersToCheck.Except(imperatorTitleHolders);

		// Don't purge animation_test characters.
		charactersToCheck = charactersToCheck
			.Where(c => !c.Id.StartsWith("animation_test_"));

		// Keep alive Imperator characters.
		charactersToCheck = charactersToCheck
			.Where(c => c is not {FromImperator: true, ImperatorCharacter.IsDead: false});

		// Make some exceptions for characters referenced in game's script files.
		charactersToCheck = charactersToCheck
			.Where(character => !character.IsNonRemovable)
			.ToArray();

		// I:R members of landed dynasties will be preserved, unless dead and childless.
		var dynastyIdsOfLandedCharacters = landedCharacters
			.Select(character => character.GetDynastyId(ck3BookmarkDate))
			.Distinct()
			.Where(id => id is not null)
			.ToFrozenSet();

		var i = 0;
		var charactersToRemove = new List<Character>();
		var parentIdsCache = new HashSet<string>();
		do {
			Logger.Debug($"Beginning iteration {i} of characters purge...");
			charactersToRemove.Clear();
			parentIdsCache.Clear();
			++i;

			// Build cache of all parent IDs.
			foreach (var character in this) {
				var motherId = character.MotherId;
				if (motherId is not null) {
					parentIdsCache.Add(motherId);
				}

				var fatherId = character.FatherId;
				if (fatherId is not null) {
					parentIdsCache.Add(fatherId);
				}
			}

			// See who can be removed.
			foreach (var character in charactersToCheck) {
				// Is the character from Imperator and do they belong to a dynasty that holds or held titles?
				if (character.FromImperator && dynastyIdsOfLandedCharacters.Contains(character.GetDynastyId(ck3BookmarkDate))) {
					// Is the character dead and childless? Purge.
					if (!parentIdsCache.Contains(character.Id)) {
						charactersToRemove.Add(character);
					}

					continue;
				}

				charactersToRemove.Add(character);
			}

			BulkRemove(charactersToRemove.ConvertAll(c => c.Id));

			Logger.Debug($"\tPurged {charactersToRemove.Count} unneeded characters in iteration {i}.");
			charactersToCheck = charactersToCheck.Except(charactersToRemove).ToArray();
		} while(charactersToRemove.Count > 0);

		// At this point we probably have many dynasties with no characters left.
		// Let's purge them.
		houses.PurgeUnneededHouses(this, ck3BookmarkDate);
		dynasties.PurgeUnneededDynasties(this, houses, ck3BookmarkDate);
		dynasties.FlattenDynastiesWithNoFounders(this, houses, ck3BookmarkDate);
	}

	public void RemoveEmployerIdFromLandedCharacters(Title.LandedTitles titles, Date conversionDate) {
		Logger.Info("Removing employer id from landed characters...");
		var landedCharacterIds = titles.GetHolderIdsForAllTitlesExceptNobleFamilyTitles(conversionDate);
		foreach (var character in this.Where(character => landedCharacterIds.Contains(character.Id))) {
			character.History.Fields["employer"].RemoveAllEntries();
		}

		Logger.IncrementProgress();
	}

	/// <summary>
	/// Distributes Imperator countries' gold among rulers and governors
	/// </summary>
	/// <param name="titles">Landed titles collection</param>
	/// <param name="config">Current configuration</param>
	public void DistributeCountriesGold(Title.LandedTitles titles, Configuration config) {
		static void AddGoldToCharacter(Character character, double gold) {
			if (character.Gold is null) {
				character.Gold = gold;
			} else {
				character.Gold += gold;
			}
		}

		Logger.Info("Distributing countries' gold...");

		var bookmarkDate = config.CK3BookmarkDate;
		var ck3CountriesFromImperator = titles.GetCountriesImportedFromImperator();
		foreach (var ck3Country in ck3CountriesFromImperator) {
			var rulerId = ck3Country.GetHolderId(bookmarkDate);
			if (rulerId == "0") {
				Logger.Debug($"Can't distribute gold in {ck3Country} because it has no holder.");
				continue;
			}

			var imperatorGold = ck3Country.ImperatorCountry!.Currencies.Gold * config.ImperatorCurrencyRate;

			var vassalCharacterIds = ck3Country.GetDeFactoVassals(bookmarkDate).Values
				.Where(vassalTitle => !vassalTitle.Landless)
				.Select(vassalTitle => vassalTitle.GetHolderId(bookmarkDate))
				.ToFrozenSet();

			var vassalCharacters = new HashSet<Character>();
			foreach (var vassalCharacterId in vassalCharacterIds) {
				if (TryGetValue(vassalCharacterId, out var vassalCharacter)) {
					vassalCharacters.Add(vassalCharacter);
				} else {
					Logger.Warn($"Character {vassalCharacterId} not found!");
				}
			}

			// Ruler should also get a share, he has double weight, so we add 2 to the count.
			var mouthsToFeedCount = vassalCharacters.Count + 2;

			var goldPerVassal = imperatorGold / mouthsToFeedCount;
			foreach (var vassalCharacter in vassalCharacters) {
				AddGoldToCharacter(vassalCharacter, goldPerVassal);
				imperatorGold -= goldPerVassal;
			}

			var ruler = this[rulerId];
			AddGoldToCharacter(ruler, imperatorGold);
		}

		Logger.IncrementProgress();
	}

	internal void ImportLegions(
		Title.LandedTitles titles,
		UnitCollection imperatorUnits,
		Imperator.Characters.CharacterCollection imperatorCharacters,
		Date date,
		UnitTypeMapper unitTypeMapper,
		IdObjectCollection<string, MenAtArmsType> menAtArmsTypes,
		ProvinceMapper provinceMapper,
		CK3LocDB ck3LocDB,
		Configuration config
	) {
		Logger.Info("Importing Imperator armies...");

		var ck3CountriesFromImperator = titles.GetCountriesImportedFromImperator();
		foreach (var ck3Country in ck3CountriesFromImperator) {
			var rulerId = ck3Country.GetHolderId(date);
			if (rulerId == "0") {
				Logger.Debug($"Can't add armies to {ck3Country} because it has no holder.");
				continue;
			}

			var imperatorCountry = ck3Country.ImperatorCountry!;
			var countryLegions = imperatorUnits.Where(u => u.CountryId == imperatorCountry.Id && u.IsArmy && u.IsLegion).ToArray();
			if (countryLegions.Length == 0) {
				continue;
			}

			var ruler = this[rulerId];

			if (config.LegionConversion == LegionConversion.MenAtArms) {
				ruler.ImportUnitsAsMenAtArms(countryLegions, date, unitTypeMapper, menAtArmsTypes, ck3LocDB);
			} else if (config.LegionConversion == LegionConversion.SpecialTroops) {
				ruler.ImportUnitsAsSpecialTroops(countryLegions, imperatorCharacters, date, unitTypeMapper, provinceMapper, ck3LocDB);
			}
		}

		Logger.IncrementProgress();
	}

	public void GenerateSuccessorsForOldCharacters(Title.LandedTitles titles, CultureCollection cultures, Date irSaveDate, Date ck3BookmarkDate, ulong randomSeed) {
		Logger.Info("Generating successors for old characters...");
		
		var oldCharacters = this
			.Where(c => c.BirthDate < ck3BookmarkDate && c.DeathDate is null && ck3BookmarkDate.DiffInYears(c.BirthDate) > 60).ToArray();

		var titleHolderIds = titles.GetHolderIdsForAllTitlesExceptNobleFamilyTitles(ck3BookmarkDate);
		
		var oldTitleHolders = oldCharacters
			.Where(c => titleHolderIds.Contains(c.Id))
			.ToArray();
		
		// For characters that don't hold any titles, just set up a death date.
		var randomForCharactersWithoutTitles = new Random((int)randomSeed);
		foreach (var oldCharacter in oldCharacters.Except(oldTitleHolders)) {
			// Roll a dice to determine how much longer the character will live.
			var yearsToLive = randomForCharactersWithoutTitles.Next(0, 30);
			
			// If the character is female and pregnant, make sure she doesn't die before the pregnancy ends.
			if (oldCharacter is {Female: true, ImperatorCharacter: not null}) {
				var lastPregnancy = oldCharacter.Pregnancies.OrderBy(p => p.BirthDate).LastOrDefault();
				if (lastPregnancy is not null) {
					oldCharacter.DeathDate = lastPregnancy.BirthDate.ChangeByYears(yearsToLive);
					continue;
				}
			}
			
			oldCharacter.DeathDate = irSaveDate.ChangeByYears(yearsToLive);
		}
		
		ConcurrentDictionary<string, Title[]> titlesByHolderId = new(titles
			.Select(t => new {Title = t, HolderId = t.GetHolderId(ck3BookmarkDate)})
			.Where(t => t.HolderId != "0")
			.GroupBy(t => t.HolderId)
			.ToDictionary(g => g.Key, g => g.Select(t => t.Title).ToArray()));

		ConcurrentDictionary<string, string[]> cultureIdToMaleNames = new(cultures
			.ToDictionary(c => c.Id, c => c.MaleNames.ToArray()));

		// For title holders, generate successors and add them to title history.
		Parallel.ForEach(oldTitleHolders, oldCharacter => {
			// Get all titles held by the character.
			var heldTitles = titlesByHolderId[oldCharacter.Id];
			string? dynastyId = oldCharacter.GetDynastyId(ck3BookmarkDate);
			string? dynastyHouseId = oldCharacter.GetDynastyHouseId(ck3BookmarkDate);
			string? faithId = oldCharacter.GetFaithId(ck3BookmarkDate);
			string? cultureId = oldCharacter.GetCultureId(ck3BookmarkDate);
			string[] maleNames;
			if (cultureId is not null) {
				maleNames = cultureIdToMaleNames[cultureId];
			} else {
				Logger.Warn($"Failed to find male names for successors of {oldCharacter.Id}.");
				maleNames = ["Alexander"];
			}
			
			var randomSeedForCharacter = randomSeed ^ (oldCharacter.ImperatorCharacter?.Id ?? 0);
			var random = new Random((int)randomSeedForCharacter);

			int successorCount = 0;
			Character currentCharacter = oldCharacter;
			Date currentCharacterBirthDate = currentCharacter.BirthDate;
			while (ck3BookmarkDate.DiffInYears(currentCharacterBirthDate) >= 90) {
				// If the character has living male children, the oldest one will be the successor.
				var successorAndBirthDate = currentCharacter.Children
					.Where(c => c is {Female: false, DeathDate: null})
					.Select(c => new { Character = c, c.BirthDate })
					.OrderBy(x => x.BirthDate)
					.FirstOrDefault();
				
				Character successor;
				Date currentCharacterDeathDate;
				Date successorBirthDate;
				if (successorAndBirthDate is not null) {
					successor = successorAndBirthDate.Character;
					successorBirthDate = successorAndBirthDate.BirthDate;
					
					// Roll a dice to determine how much longer the character will live.
					// But make sure the successor is at least 16 years old when the old character dies.
					var successorAgeAtBookmarkDate = ck3BookmarkDate.DiffInYears(successorBirthDate);
					var yearsUntilSuccessorBecomesAnAdult = Math.Max(16 - successorAgeAtBookmarkDate, 0);

					var yearsToLive = random.Next((int)Math.Ceiling(yearsUntilSuccessorBecomesAnAdult), 25);
					int currentCharacterAge = random.Next(30 + yearsToLive, 80);
					currentCharacterDeathDate = currentCharacterBirthDate.ChangeByYears(currentCharacterAge);
					// Needs to be after the save date.
					if (currentCharacterDeathDate <= irSaveDate) {
						currentCharacterDeathDate = irSaveDate.ChangeByDays(1);
					}
				} else {
					// We don't want all the generated successors on the map to have the same birth date.
					var yearsUntilHeir = random.Next(1, 5);

					// Make the old character live until the heir is at least 16 years old.
					var successorAge = random.Next(yearsUntilHeir + 16, 30);
					int currentCharacterAge = random.Next(30 + successorAge, 80);
					currentCharacterDeathDate = currentCharacterBirthDate.ChangeByYears(currentCharacterAge);
					if (currentCharacterDeathDate <= irSaveDate) {
						currentCharacterDeathDate = irSaveDate.ChangeByDays(1);
					}

					// Generate a new successor.
					string id = $"irtock3_{oldCharacter.Id}_successor_{successorCount}";
					string firstName = maleNames[random.Next(0, maleNames.Length)];

					successorBirthDate = currentCharacterDeathDate.ChangeByYears(-successorAge);
					successor = new Character(id, firstName, successorBirthDate, this) {FromImperator = true};
					Add(successor);
					if (currentCharacter.Female) {
						successor.Mother = currentCharacter;
					} else {
						successor.Father = currentCharacter;
					}
					if (cultureId is not null) {
						successor.SetCultureId(cultureId, null);
					}
					if (faithId is not null) {
						successor.SetFaithId(faithId, null);
					}
					if (dynastyId is not null) {
						successor.SetDynastyId(dynastyId, null);
					}
					if (dynastyHouseId is not null) {
						successor.SetDynastyHouseId(dynastyHouseId, null);
					}
				}

				currentCharacter.DeathDate = currentCharacterDeathDate;
				// On the old character death date, the successor should inherit all titles.
				foreach (var heldTitle in heldTitles) {
					heldTitle.SetHolder(successor, currentCharacterDeathDate);
				}

				// Move to the successor and repeat the process.
				currentCharacter = successor;
				currentCharacterBirthDate = successorBirthDate;
				++successorCount;
			}
			
			// After the loop, currentCharacter should represent the successor at bookmark date.
			// Set his DNA to avoid weird looking character on the bookmark screen in CK3.
			currentCharacter.DNA = oldCharacter.DNA;
			
			// Transfer gold to the living successor.
			currentCharacter.Gold = oldCharacter.Gold;
			oldCharacter.Gold = null;
		});
	}
	
	internal void ConvertImperatorCharacterDNA(DNAFactory dnaFactory) {
		Logger.Info("Converting Imperator character DNA to CK3...");
		foreach (var character in this) {
			if (character.ImperatorCharacter is null) {
				continue;
			}
			
			PortraitData? portraitData = character.ImperatorCharacter.PortraitData;
			if (portraitData is not null) {
				try {
					character.DNA = dnaFactory.GenerateDNA(character.ImperatorCharacter, portraitData);
				} catch (Exception e) {
					Logger.Warn($"Failed to generate DNA for character {character.Id}: {e.Message}");
				}
			}
		}
	}

	public void RemoveUndefinedTraits(TraitMapper traitMapper) {
		Logger.Info("Removing undefined traits from CK3 character history...");

		var definedTraits = traitMapper.ValidCK3TraitIDs.ToFrozenSet();
		
		foreach (var character in this) {
			if (character.FromImperator) {
				continue;
			}
			
			var traitsField = character.History.Fields["traits"];
			int removedCount = traitsField.RemoveAllEntries(value => !definedTraits.Contains(value.ToString()?.RemQuotes() ?? string.Empty));
			if (removedCount > 0) {
				Logger.Debug($"Removed {removedCount} undefined traits from character {character.Id}.");
			}
		}
	}

	public void RemoveInvalidDynastiesFromHistory(DynastyCollection dynasties) {
		Logger.Info("Removing invalid dynasties from CK3 character history...");

		var ck3Characters = this.Where(c => !c.FromImperator).ToArray();
		var validDynastyIds = dynasties.Select(d => d.Id).ToFrozenSet();

		foreach (var character in ck3Characters) {
			if (!character.History.Fields.TryGetValue("dynasty", out var dynastyField)) {
				continue;
			}

			dynastyField.RemoveAllEntries(value => {
				var dynastyId = value.ToString()?.RemQuotes();

				if (string.IsNullOrWhiteSpace(dynastyId)) {
					return true;
				}

				return !validDynastyIds.Contains(dynastyId);
			});
		}
	}
}