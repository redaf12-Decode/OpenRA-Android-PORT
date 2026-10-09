#region Copyright & License Information
/* 
 * Copyright (c) The OpenRA Developers and Contributors
 * This file is part of OpenRA, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 of
 * the License, or (at your option) any later version. For more
 * information, see COPYING.
 */
#endregion

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using OpenRA.Graphics;
using OpenRA.Mods.Common.Graphics;

namespace OpenRA.Mods.RA2.Graphics
{
	public class ExtendedTilesetSpecificSpriteSequenceLoader : DefaultSpriteSequenceLoader
	{
		public readonly string DefaultSpriteExtension = ".shp";
		public readonly Dictionary<string, string> TilesetExtensions;
		public readonly Dictionary<string, string> TilesetCodes;
		public readonly Dictionary<string, string> TilesetSuffixes;

		// The release-20230225 engine read these mappings from the SpriteSequenceFormat
		// metadata block of mod.yaml. The modern engine instantiates sequence loaders
		// without arguments and no longer exposes loader metadata, so the RA2 mod's
		// values (mods/ra2/mod.yaml, unchanged) are baked in here.
		public ExtendedTilesetSpecificSpriteSequenceLoader()
		{
			TilesetExtensions = new Dictionary<string, string>
			{
				{ "TEMPERATE", ".tem" },
				{ "SNOW", ".sno" },
				{ "URBAN", ".urb" }
			};

			TilesetCodes = new Dictionary<string, string>
			{
				{ "GENERIC", "g" },
				{ "SNOW", "a" },
				{ "TEMPERATE", "t" },
				{ "URBAN", "u" }
			};

			TilesetSuffixes = new Dictionary<string, string>
			{
				{ "SNOW", "a" }
			};
		}

		public override ISpriteSequence CreateSequence(
			ModData modData, string tileset, SpriteCache cache, string image, string sequence, MiniYaml data, MiniYaml defaults)
		{
			return new ExtendedTilesetSpecificSpriteSequence(cache, this, image, sequence, data, defaults);
		}
	}

	public class ExtendedTilesetSpecificSpriteSequence : DefaultSpriteSequence
	{
		public ExtendedTilesetSpecificSpriteSequence(SpriteCache cache, ISpriteSequenceLoader loader, string image, string sequence, MiniYaml data, MiniYaml defaults)
			: base(cache, loader, image, sequence, data, defaults) { }

		string ResolveTilesetId(string tileSet, MiniYaml data)
		{
			var yaml = LoadField("TilesetOverrides", (Dictionary<string, string>)null, data);
			if (yaml != null && yaml.TryGetValue(tileSet, out var overridden))
				tileSet = overridden;

			return tileSet;
		}

		// The release-20230225 engine resolved sprite filenames through a virtual
		// GetSpriteSrc hook. The modern engine instead resolves them through
		// ParseFilenames/ParseCombineFilenames, so the same tileset-specific
		// code/suffix/extension rules are applied to the filenames the base
		// implementation returns.
		string ApplyTilesetRules(string spriteName, string tileSet, MiniYaml data)
		{
			var loader = (ExtendedTilesetSpecificSpriteSequenceLoader)Loader;

			if (LoadField("UseTilesetCode", false, data))
			{
				if (loader.TilesetCodes.TryGetValue(ResolveTilesetId(tileSet, data), out var code) && spriteName.Length > 2)
					spriteName = spriteName.Substring(0, 1) + code + spriteName.Substring(2, spriteName.Length - 2);
			}

			if (LoadField("UseTilesetSuffix", false, data))
			{
				if (loader.TilesetSuffixes.TryGetValue(ResolveTilesetId(tileSet, data), out var tilesetSuffix))
					spriteName = spriteName + tilesetSuffix;
			}

			if (LoadField("AddExtension", true, data))
			{
				var useTilesetExtension = LoadField("UseTilesetExtension", false, data);

				if (useTilesetExtension && loader.TilesetExtensions.TryGetValue(ResolveTilesetId(tileSet, data), out var tilesetExtension))
					return spriteName + tilesetExtension;

				return spriteName + loader.DefaultSpriteExtension;
			}

			return spriteName;
		}

		protected override IEnumerable<ReservationInfo> ParseFilenames(ModData modData, string tileset, ImmutableArray<int> frames, MiniYaml data, MiniYaml defaults)
		{
			return base.ParseFilenames(modData, tileset, frames, data, defaults)
				.Select(r => new ReservationInfo(
					ApplyTilesetRules(r.Filename ?? image, tileset, data), r.LoadFrames, r.Frames, r.Location));
		}

		protected override IEnumerable<ReservationInfo> ParseCombineFilenames(ModData modData, string tileset, ImmutableArray<int> frames, MiniYaml data)
		{
			return base.ParseCombineFilenames(modData, tileset, frames, data)
				.Select(r => new ReservationInfo(
					ApplyTilesetRules(r.Filename ?? image, tileset, data), r.LoadFrames, r.Frames, r.Location));
		}
	}
}
