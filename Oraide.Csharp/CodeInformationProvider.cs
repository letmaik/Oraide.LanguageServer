﻿using System.Collections.Generic;
using System.IO;
using Oraide.Core;
using Oraide.Core.Entities.Csharp;

namespace Oraide.Csharp
{
	// The currently planned/supported use-cases for code information are:
	//  - Generating symbols to navigate to.
	//  - Referencing documentation from DescAttributes.
	// Future planned use-cases:
	//  - Validating trait and trait property existence (using the same symbols used for navigation).
	//  - Autocomplete for trait and trait property names.
	//  - Any of the above for non-trait types.
	public class CodeInformationProvider
	{
		readonly string workspaceFolderPath;
		readonly string defaultOpenRaFolderPath;
		readonly string openRaFolder;
		readonly CodeSymbolGenerationStrategy symbolGenerator;

		public CodeInformationProvider(string workspaceFolderPath, string defaultOpenRaFolderPath)
		{
			this.workspaceFolderPath = workspaceFolderPath;
			this.defaultOpenRaFolderPath = defaultOpenRaFolderPath;

			openRaFolder = GetOpenRaFolder();
			if (OpenRaFolderUtils.IsOpenRaRepositoryFolder(openRaFolder) || OpenRaFolderUtils.IsModSdkRepositoryFolder(openRaFolder))
			{
				// TODO: Strategy 1 - C# code parsing.
				symbolGenerator = new CodeParsingSymbolGenerationStrategy();
			}
			else if (OpenRaFolderUtils.IsOpenRaInstallationFolder(openRaFolder))
			{
				// TODO: Strategy 2 - DLL reflection.
				// symbolGenerator = new ReflectionSymbolGenerationStrategy();
				symbolGenerator = new FromStaticFileSymbolGenerationStrategy(); // Falling back to Strategy 3 until 2 is implemented.
			}
			else
			{
				// TODO: Strategy 3 - load data from static file.
				symbolGenerator = new FromStaticFileSymbolGenerationStrategy();
			}
		}

		public IReadOnlyDictionary<string, TraitInfo> GetTraitInfos()
		{
			return symbolGenerator.GetTraitInfos(openRaFolder);
		}

		string GetOpenRaFolder()
		{
			var oraFolderPath = "";
			if (OpenRaFolderUtils.IsOpenRaFolder(workspaceFolderPath))
				oraFolderPath = workspaceFolderPath;
			else if (OpenRaFolderUtils.IsModsFolder(workspaceFolderPath))
			{
				var parentFolder = Directory.GetParent(workspaceFolderPath)?.FullName;
				if (OpenRaFolderUtils.IsOpenRaFolder(parentFolder))
					oraFolderPath = parentFolder;
			}
			else if (OpenRaFolderUtils.IsModFolder(workspaceFolderPath))
			{
				var parentFolder = Directory.GetParent(workspaceFolderPath)?.Parent?.FullName;
				if (OpenRaFolderUtils.IsOpenRaFolder(parentFolder))
					oraFolderPath = parentFolder;
			}
			else if (OpenRaFolderUtils.IsModSubfolder(workspaceFolderPath))
			{
				var parentFolder = Directory.GetParent(workspaceFolderPath)?.Parent?.Parent?.FullName;
				if (OpenRaFolderUtils.IsOpenRaFolder(parentFolder))
					oraFolderPath = parentFolder;
			}

			if (string.IsNullOrEmpty(oraFolderPath))
			{
				if (OpenRaFolderUtils.IsOpenRaFolder(defaultOpenRaFolderPath))
					oraFolderPath = defaultOpenRaFolderPath;
			}

			return oraFolderPath;
		}
	}
}