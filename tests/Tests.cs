using System;
using System.IO;
using System.Linq;
using System.Reflection;
using StbTrueTypeSharp.Tests.Utility;
using static StbTrueTypeSharp.StbTrueType;
using Xunit;
using Xunit.Sdk;
using FontsList = System.Collections.Generic.List<StbTrueTypeSharp.StbTrueType.stbtt_fontinfo>;

namespace StbTrueTypeSharp.Tests
{
	public unsafe class Tests
	{
		private static readonly Assembly _assembly = typeof(Tests).Assembly;

		/// <summary>
		///     Makes sure a font whose cmap this library cannot read is reported by a null return
		///     rather than by an exception.
		/// </summary>
		/// <remarks>
		///     This used to throw, and the throw was the bug: a font that cannot be read is an
		///     ordinary thing for a text stack to meet -- it picks the next candidate face and
		///     carries on -- whereas an exception thrown out of font loading travels up through the
		///     shaper into whatever is laying out the page. Upstream stb_truetype reports it by
		///     returning 0 from stbtt_InitFont, CreateFont turns that into a null, and that is the
		///     contract every caller in this repository was already written against.
		/// </remarks>
		[Fact]
		public void TestNoIndexMap()
		{
			var ttfData = _assembly.ReadResourceAsBytes("DroidSans.ttf");
			var fontInfo = CreateFont(ttfData, 0);
			Assert.NotNull(fontInfo);
			var glyphA = stbtt_FindGlyphIndex(fontInfo, 'A');
			fontInfo.Dispose();

			// Platform 1 is Macintosh, which this library reads no subtable of, so the grafted font
			// has a cmap table and still no map it can use -- the same situation as a font with no
			// cmap encoding record at all, and the one the old code threw on.
			var unreadable = GraftCmap(ttfData,
				new[] { (STBTT_PLATFORM_ID_MAC, 0, BuildFormat4Subtable(new[] { (0x0041, 0x0041, glyphA) })) });
			Assert.Null(CreateFont(unreadable, 0));
		}

		/// <summary>
		///     Makes sure a font whose only cmap is a Microsoft symbol one loads, and that a request
		///     spelled either way round finds the glyph.
		/// </summary>
		/// <remarks>
		///     Wingdings, Webdings, Symbol and most icon fonts have no Unicode cmap at all, so
		///     before symbol subtables were accepted none of them would load and every run of text
		///     set in one was lost.
		/// </remarks>
		[Fact]
		public void TestSymbolCmap()
		{
			var ttfData = _assembly.ReadResourceAsBytes("DroidSans.ttf");
			var reference = CreateFont(ttfData, 0);
			Assert.NotNull(reference);
			var glyphA = stbtt_FindGlyphIndex(reference, 'A');
			var glyphB = stbtt_FindGlyphIndex(reference, 'B');
			Assert.NotEqual(0, glyphA);
			Assert.NotEqual(0, glyphB);
			reference.Dispose();

			// The one segment addressed in the private use area and the one addressed directly are
			// both there on purpose: a real symbol font maps only U+F000..U+F0FF, but the point of
			// the test is that the translation runs in both directions and only after the map has
			// been asked as it stands, so the font has to be able to answer one of each.
			var symbolTtf = GraftCmap(ttfData,
				new[]
				{
					(STBTT_PLATFORM_ID_MICROSOFT, STBTT_MS_EID_SYMBOL,
						BuildFormat4Subtable(new[] { (0x0041, 0x0041, glyphA), (0xF042, 0xF042, glyphB) }))
				});

			var fontInfo = CreateFont(symbolTtf, 0);
			Assert.NotNull(fontInfo);
			Assert.True(fontInfo.symbolCmap);

			// Asked exactly as the map is written.
			Assert.Equal(glyphA, stbtt_FindGlyphIndex(fontInfo, 0x0041));
			Assert.Equal(glyphB, stbtt_FindGlyphIndex(fontInfo, 0xF042));
			// 'B' as a document stores it, resolved through U+F000 | 0x42.
			Assert.Equal(glyphB, stbtt_FindGlyphIndex(fontInfo, 0x0042));
			// U+F041 as a symbol-aware producer writes it, resolved back down to 0x41.
			Assert.Equal(glyphA, stbtt_FindGlyphIndex(fontInfo, 0xF041));
			// Nothing the font does not have starts working.
			Assert.Equal(0, stbtt_FindGlyphIndex(fontInfo, 0x2603));

			// And the glyphs reached through the symbol map really do rasterize.
			TestRasterize(fontInfo, "AB", 32.0f);

			fontInfo.Dispose();
		}

		/// <summary>
		///     Makes sure a Unicode cmap is preferred over a symbol one even when the symbol subtable
		///     is listed after it.
		/// </summary>
		/// <remarks>
		///     The encoding records are walked in file order and a font may list both maps in either
		///     order, so the choice has to be made after the whole table has been read. Reading a
		///     font that has a perfectly good Unicode cmap through its symbol map instead would map
		///     almost nothing.
		/// </remarks>
		[Fact]
		public void TestUnicodeCmapPreferredOverSymbolCmap()
		{
			var ttfData = _assembly.ReadResourceAsBytes("DroidSans.ttf");
			var reference = CreateFont(ttfData, 0);
			Assert.NotNull(reference);
			var glyphA = stbtt_FindGlyphIndex(reference, 'A');
			var glyphB = stbtt_FindGlyphIndex(reference, 'B');
			reference.Dispose();

			var mixedTtf = GraftCmap(ttfData,
				new[]
				{
					(STBTT_PLATFORM_ID_MICROSOFT, STBTT_MS_EID_UNICODE_BMP,
						BuildFormat4Subtable(new[] { (0x0041, 0x0041, glyphA) })),
					(STBTT_PLATFORM_ID_MICROSOFT, STBTT_MS_EID_SYMBOL,
						BuildFormat4Subtable(new[] { (0xF042, 0xF042, glyphB) }))
				});

			var fontInfo = CreateFont(mixedTtf, 0);
			Assert.NotNull(fontInfo);
			Assert.False(fontInfo.symbolCmap);
			Assert.Equal(glyphA, stbtt_FindGlyphIndex(fontInfo, 'A'));
			// The symbol subtable is not the one in use, so neither it nor the private use area
			// translation answers for 'B'.
			Assert.Equal(0, stbtt_FindGlyphIndex(fontInfo, 'B'));
			fontInfo.Dispose();
		}

		/// <summary>
		///     Builds a cmap format 4 subtable mapping the given single code points to the given
		///     glyphs, plus the mandatory 0xFFFF terminating segment.
		/// </summary>
		/// <remarks>
		///     Tests that need a symbol font need one that is otherwise a real, complete font, and
		///     the honest way to get that without committing a licensed binary such as Wingdings is
		///     to graft a hand written cmap onto the test font already in the repository: every
		///     other table -- head, hhea, hmtx, loca, glyf -- stays exactly as it was, so the glyphs
		///     the map reaches are real outlines that really rasterize. Rewriting DroidSans' own
		///     format 4 subtable in place was tried first and is the wrong tool here: its segments
		///     span most of the BMP, so they cannot simply be shifted up by 0xF000 without running
		///     into the 0xFFFF terminator.
		/// </remarks>
		private static byte[] BuildFormat4Subtable((int start, int end, int glyph)[] segments)
		{
			var segCount = segments.Length + 1;
			var length = 16 + segCount * 8;
			var subtable = new byte[length];
			var searchRange = 2;
			var entrySelector = 0;
			while (searchRange * 2 <= segCount * 2)
			{
				searchRange *= 2;
				++entrySelector;
			}

			WriteUShort(subtable, 0, 4);
			WriteUShort(subtable, 2, length);
			WriteUShort(subtable, 4, 0);
			WriteUShort(subtable, 6, segCount * 2);
			WriteUShort(subtable, 8, searchRange);
			WriteUShort(subtable, 10, entrySelector);
			WriteUShort(subtable, 12, segCount * 2 - searchRange);

			var endCodes = 14;
			var startCodes = endCodes + segCount * 2 + 2;
			var idDeltas = startCodes + segCount * 2;
			var idRangeOffsets = idDeltas + segCount * 2;
			for (var i = 0; i < segments.Length; ++i)
			{
				WriteUShort(subtable, endCodes + i * 2, segments[i].end);
				WriteUShort(subtable, startCodes + i * 2, segments[i].start);
				// The segment is walked as glyph = code + idDelta, so a segment covering one code
				// point carries the difference between the two.
				WriteUShort(subtable, idDeltas + i * 2, (segments[i].glyph - segments[i].start) & 0xFFFF);
				WriteUShort(subtable, idRangeOffsets + i * 2, 0);
			}

			// The format requires the last segment to end at 0xFFFF; an idDelta of 1 wraps it round
			// to glyph 0, which is what a lookup of an unmapped code point is supposed to return.
			WriteUShort(subtable, endCodes + segments.Length * 2, 0xFFFF);
			WriteUShort(subtable, startCodes + segments.Length * 2, 0xFFFF);
			WriteUShort(subtable, idDeltas + segments.Length * 2, 1);
			WriteUShort(subtable, idRangeOffsets + segments.Length * 2, 0);
			return subtable;
		}

		/// <summary>
		///     Returns a copy of a font whose cmap table has been replaced by one listing the given
		///     (platform, encoding, subtable) records in that order.
		/// </summary>
		/// <remarks>
		///     The replacement is appended to the end of the file and the table directory entry
		///     repointed at it, which leaves every offset in the original font untouched. The
		///     directory's checksums are left alone because this library never reads them, as
		///     neither does any rasterizer -- they are there for installers.
		/// </remarks>
		private static byte[] GraftCmap(byte[] ttf, (int platform, int encoding, byte[] subtable)[] records)
		{
			var cmapLength = 4 + records.Length * 8;
			foreach (var record in records)
				cmapLength += record.subtable.Length;

			// sfnt offsets are conventionally four byte aligned; the font is padded rather than the
			// table pushed around so that the original bytes keep the positions they had.
			var start = (ttf.Length + 3) & ~3;
			var result = new byte[start + cmapLength];
			Buffer.BlockCopy(ttf, 0, result, 0, ttf.Length);

			WriteUShort(result, start, 0);
			WriteUShort(result, start + 2, records.Length);
			var subtableOffset = 4 + records.Length * 8;
			for (var i = 0; i < records.Length; ++i)
			{
				var encodingRecord = start + 4 + i * 8;
				WriteUShort(result, encodingRecord, records[i].platform);
				WriteUShort(result, encodingRecord + 2, records[i].encoding);
				WriteULong(result, encodingRecord + 4, subtableOffset);
				Buffer.BlockCopy(records[i].subtable, 0, result, start + subtableOffset,
					records[i].subtable.Length);
				subtableOffset += records[i].subtable.Length;
			}

			int numTables = (result[4] << 8) | result[5];
			for (var i = 0; i < numTables; ++i)
			{
				var entry = 12 + i * 16;
				if (result[entry] != 'c' || result[entry + 1] != 'm' || result[entry + 2] != 'a' ||
					result[entry + 3] != 'p')
					continue;

				WriteULong(result, entry + 8, start);
				WriteULong(result, entry + 12, cmapLength);
				return result;
			}

			throw new InvalidOperationException("The test font has no cmap table to replace.");
		}

		private static void WriteUShort(byte[] data, int offset, int value)
		{
			data[offset] = (byte)((value >> 8) & 0xFF);
			data[offset + 1] = (byte)(value & 0xFF);
		}

		private static void WriteULong(byte[] data, int offset, int value)
		{
			data[offset] = (byte)((value >> 24) & 0xFF);
			data[offset + 1] = (byte)((value >> 16) & 0xFF);
			data[offset + 2] = (byte)((value >> 8) & 0xFF);
			data[offset + 3] = (byte)(value & 0xFF);
		}

		[Fact]
		public void TestCreationAndDispose()
		{
			var ttfData = _assembly.ReadResourceAsBytes("DroidSans.ttf");
			var fontInfo = CreateFont(ttfData, 0);
			Assert.NotNull(fontInfo);
			Assert.True(fontInfo.isDataCopy);
			fontInfo.Dispose();
			Assert.True(fontInfo.data == null);
		}

		[Fact]
		public unsafe void TestLoadFontCollection()
		{
			string fontsPath = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
			// Environment.SpecialFolder.Fonts is empty on platforms that have no such notion, and
			// enumerating "" throws rather than returning nothing.
			if (string.IsNullOrEmpty(fontsPath) || !Directory.Exists(fontsPath))
				return;

			string someTtcPath = Directory.EnumerateFiles(fontsPath, "*.ttc", new EnumerationOptions()
			{
				AttributesToSkip = FileAttributes.Directory,
				MatchCasing = MatchCasing.CaseInsensitive,
				MatchType = MatchType.Simple,
				RecurseSubdirectories = false,
				IgnoreInaccessible = true,
				ReturnSpecialDirectories = false
			}).FirstOrDefault();

			if (someTtcPath == null)
				throw new SkipException("You don't have a ttc font installed on your computer, but this test requires it.");

			byte[] ttcContent = File.ReadAllBytes(someTtcPath);
			// Assert.NotNull(someTtc);
			FontsList fonts;
			int numberOfFonts;

			fixed (byte* ttcPtr = ttcContent)
			{
				numberOfFonts = stbtt_GetNumberOfFonts(ttcPtr);
				fonts = new FontsList(numberOfFonts);
				for (int i = 0; i < numberOfFonts; i++)
				{
					int offset = stbtt_GetFontOffsetForIndex(ttcPtr, i);
					fonts.Add(CreateFont(ttcContent, offset));
				}
			}

			Assert.Equal(numberOfFonts, fonts.Count);
		}

		private void TestRasterize(stbtt_fontinfo fontInfo, string text, float size)
		{
			int iascent, idescent, ilineGap;
			stbtt_GetFontVMetrics(fontInfo, &iascent, &idescent, &ilineGap);

			var scale = stbtt_ScaleForPixelHeight(fontInfo, 32.0f);
			var ascent = iascent * scale;
			var descent = idescent * scale;
			var lineGap = ilineGap * scale;

			var lineHeight = ascent - descent + lineGap;

			Assert.True(lineHeight.EpsilonEquals(32.0f));

			for (var i = 0; i < text.Length; ++i)
			{
				var c = text[i];

				if (char.IsWhiteSpace(c))
				{
					continue;
				}

				var glyphId = stbtt_FindGlyphIndex(fontInfo, c);
				Assert.NotEqual(0, glyphId);

				int advanceWidth, leftSideBearing;
				stbtt_GetGlyphHMetrics(fontInfo, glyphId, &advanceWidth, &leftSideBearing);

				int x0, y0, x1, y1;
				stbtt_GetGlyphBitmapBox(fontInfo, glyphId, scale, scale, &x0, &y0, &x1, &y1);

				var width = x1 - x0;
				var height = y1 - y0;

				Assert.NotEqual(0, width);
				Assert.NotEqual(0, height);
				var data = new byte[width * height];

				fixed (byte* ptr = data)
				{
					stbtt_MakeGlyphBitmap(fontInfo, ptr, width, height, width, scale, scale, glyphId);
				}
			}
		}

		[Fact]
		public void TestNewRasterizer()
		{
			var ttfData = _assembly.ReadResourceAsBytes("DroidSans.ttf");
			var fontInfo = CreateFont(ttfData, 0);
			Assert.NotNull(fontInfo);
			Assert.True(fontInfo.isDataCopy);

			TestRasterize(fontInfo, "Hello, World!", 32.0f);

			Assert.False(usedOldRasterizer);

			fontInfo.Dispose();
			Assert.True(fontInfo.data == null);
		}

		[Fact]
		public void TestOldRasterizer()
		{
			var ttfData = _assembly.ReadResourceAsBytes("DroidSans.ttf");
			var fontInfo = CreateFont(ttfData, 0);
			Assert.NotNull(fontInfo);
			Assert.True(fontInfo.isDataCopy);

			fontInfo.useOldRasterizer = true;

			TestRasterize(fontInfo, "Hello, World!", 32.0f);

			Assert.True(usedOldRasterizer);

			fontInfo.Dispose();
			Assert.True(fontInfo.data == null);
		}
	}
}