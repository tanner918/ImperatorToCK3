﻿using commonItems;
using commonItems.Collections;

namespace ImperatorToCK3.Imperator.Pops;

public sealed class Pop : IIdentifiable<ulong> {
	public ulong Id { get; } = 0;
	public string Type { get; set; } = "";
	public string Culture { get; set; } = "";
	public string Religion { get; set; } = "";
	public Pop(ulong id) {
		Id = id;
	}
	
	public static Pop Parse(string idString, BufferedReader reader) {
		var newPop = new Pop(ulong.Parse(idString));

		var parser = new Parser();
		parser.RegisterKeyword("type", r => newPop.Type = string.Intern(r.GetString()));
		parser.RegisterKeyword("culture", r => newPop.Culture = string.Intern(r.GetString()));
		parser.RegisterKeyword("religion", r => newPop.Religion = string.Intern(r.GetString()));
		parser.RegisterRegex(CommonRegexes.Catchall, ParserHelpers.IgnoreAndLogItem);
		parser.ParseStream(reader);
		
		return newPop;
	}
}