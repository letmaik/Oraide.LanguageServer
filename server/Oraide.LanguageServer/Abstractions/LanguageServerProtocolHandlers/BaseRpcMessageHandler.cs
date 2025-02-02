﻿using System;
using System.Linq;
using System.Text.RegularExpressions;
using LspTypes;
using Oraide.Core.Entities;
using Oraide.Core.Entities.Csharp;
using Oraide.Core.Entities.MiniYaml;
using Oraide.LanguageServer.Caching;

namespace Oraide.LanguageServer.Abstractions.LanguageServerProtocolHandlers
{
	public abstract class BaseRpcMessageHandler : IRpcMessageHandler
	{
		protected static readonly object LockObject = new object();

		protected readonly bool trace = true;
		protected readonly SymbolCache symbolCache;
		protected readonly OpenFileCache openFileCache;

		protected BaseRpcMessageHandler(SymbolCache symbolCache, OpenFileCache openFileCache)
		{
			this.symbolCache = symbolCache;
			this.openFileCache = openFileCache;
		}

		protected virtual bool TryGetCursorTarget(TextDocumentPositionParams positionParams, out CursorTarget target)
		{
			var filePath = positionParams.TextDocument.Uri;
			var targetLineIndex = (int)positionParams.Position.Line;
			var targetCharacterIndex = (int)positionParams.Position.Character;

			// Determine file type.
			var fileType = FileType.Unknown;
			if (filePath.Contains("/rules/") || (filePath.Contains("/maps/") && !filePath.EndsWith("map.yaml")))
				fileType = FileType.Rules;
			else if (filePath.Contains("/weapons/"))
				fileType = FileType.Weapons;

			if (!openFileCache.ContainsFile(filePath))
			{
				target = default;
				return false;
			}

			var (fileLines, fileNodes) = openFileCache[filePath];

			var targetLine = fileLines[targetLineIndex];
			var pre = targetLine.Substring(0, targetCharacterIndex);
			var post = targetLine.Substring(targetCharacterIndex);

			if ((string.IsNullOrWhiteSpace(pre) && (post[0] == '\t' || post[0] == ' '))
			    || string.IsNullOrWhiteSpace(post))
			{
				target = default;
				return false;
			}

			var targetNode = fileNodes[targetLineIndex];

			string sourceString;
			string targetType;

			if (pre.Contains(':'))
			{
				targetType = "value";
				sourceString = targetNode.Value;
			}
			else
			{
				if (pre.Contains('@'))
				{
					targetType = "keyIdentifier";
					sourceString = targetNode.Key.Split('@')[1];
				}
				else
				{
					targetType = "key";
					sourceString = targetNode.Key.Split('@')[0];
				}
			}

			if (!TryGetTargetString(targetLine, targetCharacterIndex, sourceString, out var targetString, out var startIndex, out var endIndex))
			{
				target = default;
				return false;
			}

			// idk at this point
			if (string.IsNullOrWhiteSpace(targetString))
				targetString = sourceString;

			TryGetModId(positionParams.TextDocument.Uri, out var modId);
			TryGetTargetStringIndentation(targetNode, out var indentation);
			target = new CursorTarget(modId, fileType, targetNode, targetType, targetString,
				new MemberLocation(filePath, targetLineIndex, startIndex),
				new MemberLocation(filePath, targetLineIndex, endIndex), indentation);

			return true;
		}

		protected virtual void Initialize(CursorTarget cursorTarget) { }

		#region CursorTarget handlers

		protected virtual object HandlePositionalRequest(TextDocumentPositionParams positionParams)
		{
			if (!TryGetCursorTarget(positionParams, out var cursorTarget))
				return null;

			Initialize(cursorTarget);

			return cursorTarget.FileType switch
			{
				FileType.Rules => HandleRulesFile(cursorTarget),
				FileType.Weapons => HandleWeaponFile(cursorTarget),
				_ => null
			};
		}

		protected virtual object HandleRulesFile(CursorTarget cursorTarget)
		{
			return cursorTarget.TargetType switch
			{
				"key" => HandleRulesKey(cursorTarget),
				"value" => HandleRulesValue(cursorTarget),
				_ => null
			};
		}

		protected virtual object HandleWeaponFile(CursorTarget cursorTarget)
		{
			return cursorTarget.TargetType switch
			{
				"key" => HandleWeaponKey(cursorTarget),
				"value" => HandleWeaponValue(cursorTarget),
				_ => null
			};
		}

		protected virtual object HandleRulesKey(CursorTarget cursorTarget) { return null; }

		protected virtual object HandleRulesValue(CursorTarget cursorTarget) { return null; }

		protected virtual object HandleWeaponKey(CursorTarget cursorTarget) { return null; }

		protected virtual object HandleWeaponValue(CursorTarget cursorTarget) { return null; }

		#endregion

		protected bool TryGetCodeMemberLocation(YamlNode targetNode, string targetString, out TraitInfo traitInfo, out MemberLocation location)
		{
			// Try treating the target string as a trait name.
			var traitName = targetString.Split('@')[0];
			if (TryGetTraitInfo(traitName, out traitInfo))
			{
				location = traitInfo.Location;
				return trace;
			}

			// Assuming we are targeting a trait property, search for a trait based on the parent node.
			traitName = targetNode.ParentNode?.Key.Split('@')[0];
			if (TryGetTraitInfo(traitName, out traitInfo))
			{
				if (CheckTraitInheritanceTree(traitInfo, targetString, out var inheritedTraitInfo, out var propertyLocation))
				{
					traitInfo = inheritedTraitInfo;
					location = propertyLocation;
					return true;
				}
			}

			location = default;
			return false;
		}

		protected bool TryGetTraitInfo(string traitName, out TraitInfo traitInfo, bool addInfoSuffix = true)
		{
			// TODO: HACK HACK HACK - There should be a better way to do this...
			var cache = symbolCache.ModSymbols.First().Value;

			var searchString = addInfoSuffix ? $"{traitName}Info" : traitName;
			if (cache.TraitInfos.Contains(searchString))
			{
				// Using .First() is not great but we have no way to differentiate between traits of the same name
				// until the server learns the concept of a mod and loaded assemblies.
				traitInfo = cache.TraitInfos[searchString].First();
				return true;
			}

			traitInfo = default;
			return false;
		}

		protected bool TryGetModId(string fileUri, out string modId)
		{
			var match = Regex.Match(fileUri, "(\\/mods\\/[^\\/]*\\/)").Value;
			modId = match.Split('/')[2];
			return true;
		}

		protected bool TryGetTargetStringIndentation(YamlNode yamlNode, out int indentation)
		{
			indentation = 0;
			var node = yamlNode;
			while (node.ParentNode != null)
			{
				node = node.ParentNode;
				indentation++;
			}

			return true;
		}

		bool TryGetTargetString(string targetLine, int targetCharacterIndex, string sourceString, out string targetString, out int startIndex, out int endIndex)
		{
			targetString = string.Empty;
			startIndex = 0;
			endIndex = 1;

			var hasReached = false;
			while (endIndex < sourceString.Length)
			{
				if (endIndex == targetCharacterIndex - targetLine.LastIndexOf(sourceString, StringComparison.InvariantCulture))
					hasReached = true;

				if (sourceString[endIndex] == ',' || endIndex == sourceString.Length - 1)
				{
					if (!hasReached)
						startIndex = endIndex;
					else
					{
						targetString = sourceString.Substring(startIndex, endIndex - startIndex + 1).Trim(' ', '\t', ',', '!');
						Console.Error.WriteLine(targetString);
						break;
					}
				}

				endIndex++;
			}

			startIndex = targetLine.IndexOf(targetString, StringComparison.InvariantCulture);
			endIndex = startIndex + targetString.Length;
			return true;
		}

		bool CheckTraitInheritanceTree(TraitInfo traitInfo, string propertyName, out TraitInfo targetTrait, out MemberLocation location)
		{
			TraitInfo? resultTrait = null;
			MemberLocation? resultLocation = null;

			// The property may be a field of the TraitInfo...
			if (traitInfo.TraitPropertyInfos.Any(x => x.Name == propertyName))
			{
				var property = traitInfo.TraitPropertyInfos.FirstOrDefault(x => x.Name == propertyName);
				resultTrait = traitInfo;
				resultLocation = property.Location;
			}
			else
			{
				// ... or it could be inherited.
				foreach (var inheritedType in traitInfo.BaseTypes)
					if (TryGetTraitInfo(inheritedType, out var inheritedTraitInfo, false))
						if (CheckTraitInheritanceTree(inheritedTraitInfo, propertyName, out targetTrait, out var inheritedLocation))
						{
							resultTrait = targetTrait;
							resultLocation = inheritedLocation;
						}
			}

			if (resultLocation == null)
			{
				targetTrait = default;
				location = default;
				return false;
			}

			targetTrait = resultTrait.Value;
			location = resultLocation.Value;
			return true;
		}
	}
}
