using System;

namespace ImperatorToCK3.CK3.Titles;

public static class TitleRankUtils {
	public static TitleRank CharToTitleRank(char rankChar) {
		return rankChar switch {
			'e' => TitleRank.empire,
			'k' => TitleRank.kingdom,
			'd' => TitleRank.duchy,
			'c' => TitleRank.county,
			'b' => TitleRank.barony,
			_ => throw new ArgumentOutOfRangeException(nameof(rankChar), $"Unknown title rank character: {rankChar}")
		};
	}
}