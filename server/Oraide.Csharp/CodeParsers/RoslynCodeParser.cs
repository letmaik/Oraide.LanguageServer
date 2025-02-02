﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Oraide.Core.Entities;
using Oraide.Core.Entities.Csharp;

namespace Oraide.Csharp.CodeParsers
{
	public static class RoslynCodeParser
	{
		public static (ILookup<string, TraitInfo>, WeaponInfo) Parse(in string oraFolderPath)
		{
			var traitInfos = new List<TraitInfo>();
			var weaponInfoFields = Array.Empty<ClassFieldInfo>();
			var warheadInfos = new List<SimpleClassInfo>();
			var projectileInfos = new List<SimpleClassInfo>();

			var filePaths = Directory.EnumerateFiles(oraFolderPath, "*.cs", SearchOption.AllDirectories)
				.Where(x => !x.Contains("OpenRA.Test"));

			foreach (var filePath in filePaths)
			{
				var fileText = File.ReadAllText(filePath);
				var syntaxTree = CSharpSyntaxTree.ParseText(fileText);
				var root = syntaxTree.GetCompilationUnitRoot();

				foreach (var element in root.Members)
				{
					if (element is NamespaceDeclarationSyntax namespaceElement)
					{
						foreach (var namespaceMember in namespaceElement.Members)
						{
							if (namespaceMember is ClassDeclarationSyntax classDeclaration)
							{
								var baseTypes = ParseBaseTypes(classDeclaration).ToArray();

								// Parsing files the "if it quacks like a duck" way because the alternative would be
								// to create a whole type hierarchy to know what inherits what and that is just overkill.
								// TODO: Be smarter about how we decide what type of file this is.
								if ((filePath.Contains("Trait")
									&& (classDeclaration.Identifier.ValueText.EndsWith("Info") || classDeclaration.Identifier.ValueText.EndsWith("InfoBase")))
									|| baseTypes.Any(x => x == "TraitInfo" || x.StartsWith("TraitInfo<")))
								{
									traitInfos.AddRange(ParseTraitInfo(filePath, fileText, classDeclaration, baseTypes));
								}
								else if (filePath.Replace("\\", "/").EndsWith("GameRules/WeaponInfo.cs") && classDeclaration.Identifier.ValueText == "WeaponInfo")
								{
									weaponInfoFields = ParseWeaponInfo(filePath, fileText, classDeclaration).ToArray();
								}
								else if (baseTypes.Any(x => x == "IProjectileInfo"))
								{
									var projectileInfo = ParseProjectileInfo(filePath, fileText, classDeclaration);
									projectileInfos.Add(projectileInfo);
								}

								// Could be done smarter, but several levels of inheritance and not wanting to construct a full tree got us here.
								else if (baseTypes.Any(x => x.EndsWith("Warhead")))
								{
									var warheadInfo = ParseWarheadInfo(filePath, fileText, classDeclaration);
									warheadInfos.Add(warheadInfo);
								}
							}
						}
					}
				}
			}

			// Resolve trait inheritance - load base types and a full list in fields - inherited or not.
			var finalTraitInfos = new List<TraitInfo>(traitInfos.Count);
			foreach (var ti in traitInfos)
			{
				// Skip the base TraitInfo class(es).
				if (ti.TraitInfoName == "TraitInfo")
					continue;

				var baseTypes = GetTraitBaseTypes(traitInfos, ti.TraitInfoName).ToArray();
				if (baseTypes.Any(x => x.TypeName == "TraitInfo"))
				{
					var fieldInfos = new List<ClassFieldInfo>();
					foreach (var (className, classFieldNames) in baseTypes)
					{
						foreach (var typeFieldName in classFieldNames)
						{
							var fi = ti.TraitPropertyInfos.FirstOrDefault(z => z.Name == typeFieldName);
							if (fi.Name != null)
								fieldInfos.Add(new ClassFieldInfo(fi.Name, fi.Type, fi.DefaultValue, className, fi.Location, fi.Description, fi.OtherAttributes));
							else
							{
								var otherFieldInfo = traitInfos.First(x => x.TraitInfoName == className).TraitPropertyInfos.First(x => x.Name == typeFieldName);
								fieldInfos.Add(new ClassFieldInfo(otherFieldInfo.Name, otherFieldInfo.Type, otherFieldInfo.DefaultValue, className, otherFieldInfo.Location, otherFieldInfo.Description, otherFieldInfo.OtherAttributes));
							}
						}
					}

					var traitInfo = new TraitInfo(
						ti.TraitName,
						ti.TraitInfoName,
						ti.TraitDescription,
						ti.Location,
						baseTypes.Where(x => x.TypeName != ti.TraitInfoName).Select(x => x.TypeName).ToArray(),
						fieldInfos.ToArray());

					finalTraitInfos.Add(traitInfo);
				}
			}

			// Resolve warhead inheritance - load base types and a full list in fields - inherited or not.
			var finalWarheadInfos = new List<SimpleClassInfo>(warheadInfos.Count);
			foreach (var wi in warheadInfos)
			{
				// Skip the base Warhead class (its Name is empty because we trim down "Warhead" off of names).
				if (wi.Name == string.Empty)
					continue;

				var baseTypes = GetWarheadBaseTypes(warheadInfos, wi.InfoName).ToArray();
				var fieldInfos = new List<ClassFieldInfo>();
				foreach (var (className, classFieldNames) in baseTypes)
				{
					foreach (var typeFieldName in classFieldNames)
					{
						var fi = wi.PropertyInfos.FirstOrDefault(z => z.Name == typeFieldName);
						if (fi.Name != null)
							fieldInfos.Add(new ClassFieldInfo(fi.Name, fi.Type, fi.DefaultValue, className, fi.Location, fi.Description, fi.OtherAttributes));
						else
						{
							var otherFieldInfo = warheadInfos.First(x => x.InfoName == className).PropertyInfos.First(x => x.Name == typeFieldName);
							fieldInfos.Add(new ClassFieldInfo(otherFieldInfo.Name, otherFieldInfo.Type, otherFieldInfo.DefaultValue, className, otherFieldInfo.Location, otherFieldInfo.Description, otherFieldInfo.OtherAttributes));
						}
					}
				}

				var warheadInfo = new SimpleClassInfo(
					wi.Name,
					wi.InfoName,
					wi.Description,
					wi.Location,
					baseTypes.Where(x => x.TypeName != wi.InfoName).Select(x => x.TypeName).ToArray(),
					fieldInfos.ToArray());

				finalWarheadInfos.Add(warheadInfo);
			}

			var weaponInfo = new WeaponInfo(weaponInfoFields, projectileInfos.ToArray(), finalWarheadInfos.ToArray());
			return (finalTraitInfos.ToLookup(x => x.TraitInfoName, y => y), weaponInfo);
		}

		// Files can potentially contain multiple TraitInfos.
		static IEnumerable<TraitInfo> ParseTraitInfo(string filePath, string fileText, ClassDeclarationSyntax classDeclaration, string[] baseTypes)
		{
			var traitProperties = new List<ClassFieldInfo>();
			var traitInfoName = classDeclaration.Identifier.ValueText;

			// Skip classes that are not TraitInfos. Make a special case exception for TooltipInfoBase.
			if (!traitInfoName.EndsWith("Info") && !traitInfoName.EndsWith("InfoBase"))
				yield break;

			// Get trait's DescAttribute.
			var traitDesc = ParseClassDescAttribute(classDeclaration);

			// Get TraitInfo property (actually field) list.
			foreach (var member in classDeclaration.Members)
				if (member is FieldDeclarationSyntax fieldMember)
					traitProperties.AddRange(ParseClassField(filePath, fileText, fieldMember));

			// Some manual string nonsense to determine trait name location inside the file.
			var classStart = classDeclaration.GetLocation().SourceSpan.Start;
			var classLocation = FindClassLocationInText(filePath, fileText, traitInfoName, classStart);

			yield return new TraitInfo(traitInfoName.Substring(0, traitInfoName.Length - 4), traitInfoName,
				traitDesc, classLocation, baseTypes, traitProperties.ToArray());
		}

		static IEnumerable<ClassFieldInfo> ParseWeaponInfo(string filePath, string fileText, ClassDeclarationSyntax classDeclaration)
		{
			return ParseClassFields(filePath, fileText, classDeclaration);
		}

		static SimpleClassInfo ParseProjectileInfo(string filePath, string fileText, ClassDeclarationSyntax classDeclaration)
		{
			return ParseSimpleClass(filePath, fileText, classDeclaration);
		}

		static SimpleClassInfo ParseWarheadInfo(string filePath, string fileText, ClassDeclarationSyntax classDeclaration)
		{
			return ParseSimpleClass(filePath, fileText, classDeclaration);
		}

		static SimpleClassInfo ParseSimpleClass(string filePath, string fileText, ClassDeclarationSyntax classDeclaration)
		{
			var projectileInfoName = classDeclaration.Identifier.ValueText;
			var projectileName = projectileInfoName.EndsWith("Info") ? projectileInfoName.Substring(0, projectileInfoName.Length - 4) : projectileInfoName;
			projectileName = projectileName.EndsWith("Warhead") ? projectileName.Substring(0, projectileName.Length - 7) : projectileName;
			var description = ParseClassDescAttribute(classDeclaration);

			// Some manual string nonsense to determine the class name location inside the file.
			var classStart = classDeclaration.GetLocation().SourceSpan.Start;
			var classLocation = FindClassLocationInText(filePath, fileText, projectileName, classStart);
			var baseTypes = ParseBaseTypes(classDeclaration).ToArray();
			var fields = ParseClassFields(filePath, fileText, classDeclaration).ToArray();

			return new SimpleClassInfo(projectileName, projectileInfoName, description, classLocation, baseTypes, fields);
		}

		static IEnumerable<ClassFieldInfo> ParseClassFields(string filePath, string fileText, ClassDeclarationSyntax classDeclaration)
		{
			// Get property (actually field) list.
			foreach (var member in classDeclaration.Members)
				if (member is FieldDeclarationSyntax fieldMember)
					foreach (var fieldInfo in ParseClassField(filePath, fileText, fieldMember))
						yield return fieldInfo;
		}

		static IEnumerable<ClassFieldInfo> ParseClassField(string filePath, string fileText, FieldDeclarationSyntax fieldDeclarationSyntax)
		{
			foreach (var variableDeclaratorSyntax in fieldDeclarationSyntax.Declaration.Variables)
			{
				var fieldDesc = "";
				var otherAttributes = new List<(string Name, string Value)>();
				foreach (var attributeList in fieldDeclarationSyntax.AttributeLists)
				{
					foreach (var attribute in attributeList.Attributes)
					{
						var attributeName = attribute.Name.GetText().ToString();
						var attributeValue = attribute.ArgumentList?.Arguments.ToString();

						if (attributeName == "Desc")
						{
							var strings = attribute.ArgumentList?.Arguments
								.Select(x => x.GetText().ToString())
								.Select(x => x.Substring(x.IndexOf('"') + 1))
								.Select(x => x.Substring(0, x.Length - 1));

							if (strings != null)
								fieldDesc = string.Join(" ", strings);

							// Resolve `nameof(...)`.
							fieldDesc = Regex.Replace(fieldDesc, "(\"\\s*\\+\\s*nameof\\(([A-Za-z0-9.\\S]*)\\)\\s*\\+\\s*\")", "$2");
						}

						// Full set of attributes on trait properties for future reference.
						else if (attributeName == "FieldLoader.LoadUsing"
						         || attributeName == "FieldLoader.Require"
						         || attributeName == "FieldLoader.Ignore"
						         || attributeName == "ActorReference"
						         || attributeName == "VoiceReference"
						         || attributeName == "VoiceSetReference"
						         || attributeName == "CursorReference"
						         || attributeName == "WeaponReference"
						         || attributeName == "PaletteReference"
						         || attributeName == "PaletteDefinition"
						         || attributeName == "SequenceReference"
						         || attributeName == "NotificationReference"
						         || attributeName == "GrantedConditionReference"
						         || attributeName == "ConsumedConditionReference"
						         || attributeName == "LocomotorReference")
						{
							// Try to resolve `nameof(...)`.
							if (attributeValue != null)
								attributeValue = Regex.Replace(attributeValue, "(nameof\\(([A-Za-z0-9.\\S]*)\\))", "$2");

							otherAttributes.Add((attributeName, attributeValue));
						}
						else
						{
							Console.Error.WriteLine($"Unknown field attribute {attributeName} in {filePath}");
						}
					}
				}

				var propertyName = variableDeclaratorSyntax.Identifier.ValueText;
				var propertyType = HumanReadablePropertyType(fieldDeclarationSyntax.Declaration.Type);
				var defaultValue = HumanReadablePropertyDefaultValue(variableDeclaratorSyntax);
				var location = FindPropertyLocationInText(filePath, fileText, variableDeclaratorSyntax.GetLocation().SourceSpan.Start);

				// Using "???" as class name here as a temporary placeholder. That should be replaced later when resolving inheritance and inherited fields.
				yield return new ClassFieldInfo(propertyName, propertyType, defaultValue, "???", location, fieldDesc, otherAttributes.ToArray());
			}
		}

		static string ParseClassDescAttribute(ClassDeclarationSyntax classDeclaration)
		{
			var description = "";
			foreach (var attributeList in classDeclaration.AttributeLists)
			{
				foreach (var attribute in attributeList.Attributes)
				{
					if (attribute.Name.GetText().ToString() == "Desc")
					{
						var strings = attribute.ArgumentList.Arguments
							.Select(x => x.GetText().ToString())
							.Select(x => x.Substring(x.IndexOf('"') + 1))
							.Select(x => x.Substring(0, x.Length - 1));

						description = string.Join(" ", strings);
					}
				}
			}

			// Resolve `nameof(...)`.
			description = Regex.Replace(description, "(\"\\s*\\+\\s*nameof\\(([A-Za-z0-9.\\S]*)\\)\\s*\\+\\s*\")", "$2");

			return description;
		}

		static IEnumerable<string> ParseBaseTypes(ClassDeclarationSyntax classDeclaration)
		{
			// Get inherited/implemented types.
			if (classDeclaration.BaseList != null)
			{
				// TODO: It would be useful to know what the `Requires` requires.
				foreach (var baseTypeSyntax in classDeclaration.BaseList.Types)
				{
					if (baseTypeSyntax.Type is IdentifierNameSyntax identifierNameSyntax)
						yield return identifierNameSyntax.Identifier.ValueText;

					if (baseTypeSyntax.Type is GenericNameSyntax genericNameSyntax)
						yield return genericNameSyntax.Identifier.ValueText;
				}
			}
		}

		static MemberLocation FindClassLocationInText(string filePath, string text, string traitInfoName, int definitionStartIndex)
		{
			var subtext = text.Substring(0, definitionStartIndex);
			subtext += text.Substring(definitionStartIndex, text.IndexOf($"class {traitInfoName}", definitionStartIndex, StringComparison.InvariantCulture) - definitionStartIndex);
			var lines = subtext.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
			var lineNumber = lines.Length;
			var characterNumber = lines.Last().Length + 6;
			return new MemberLocation(filePath, lineNumber, characterNumber);
		}

		static MemberLocation FindPropertyLocationInText(string filePath, string text, int definitionStartIndex)
		{
			var subtext = text.Substring(0, definitionStartIndex);
			var lines = subtext.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
			var lineNumber = lines.Length;
			var characterNumber = lines.Last().Replace('\t', ' ').Length;
			return new MemberLocation(filePath, lineNumber, characterNumber);
		}

		static string HumanReadablePropertyType(TypeSyntax typeSyntax)
		{
			var propertyType = string.Empty;
			try
			{
				if (typeSyntax is PredefinedTypeSyntax predefinedTypeSyntax)
					propertyType = predefinedTypeSyntax.Keyword.Text;
				else if (typeSyntax is GenericNameSyntax genericNameSyntax)
					propertyType = $"{genericNameSyntax.Identifier.Value} of {genericNameSyntax.TypeArgumentList.Arguments}";
				else if (typeSyntax is IdentifierNameSyntax identifierNameSyntax)
					propertyType = identifierNameSyntax.Identifier.Text;
			}
			catch (Exception e)
			{
				Console.Error.WriteLine(e);
			}

			return propertyType;
		}

		static string HumanReadablePropertyDefaultValue(VariableDeclaratorSyntax declaratorSyntax)
		{
			var defaultValue = string.Empty;
			try
			{
				if (declaratorSyntax.Initializer == null)
					return defaultValue;

				defaultValue = declaratorSyntax.Initializer.Value.ToString();
				var valueKind = declaratorSyntax.Initializer.Value.Kind();
				if (valueKind == SyntaxKind.BitwiseOrExpression)
				{
					var values = new List<string>();
					var binaryExpression = (BinaryExpressionSyntax)declaratorSyntax.Initializer.Value;
					while (true)
					{
						var leftExpression = binaryExpression.Left;
						if (leftExpression is MemberAccessExpressionSyntax leftAccessExpression)
						{
							values.Add(((MemberAccessExpressionSyntax)binaryExpression.Right).Name.Identifier.Text);
							values.Add(leftAccessExpression.Name.Identifier.Text);
							break;
						}

						if (leftExpression is BinaryExpressionSyntax leftBinaryExpression)
						{
							values.Add(((MemberAccessExpressionSyntax)binaryExpression.Right).Name.Identifier.Text);
							binaryExpression = leftBinaryExpression;
						}
					}

					values.Reverse();
					defaultValue = string.Join(", ", values);
				}
				else if (valueKind == SyntaxKind.SimpleMemberAccessExpression)
					defaultValue = ((MemberAccessExpressionSyntax)declaratorSyntax.Initializer.Value).Name.Identifier.Text;
				else if (valueKind == SyntaxKind.ObjectCreationExpression)
				{
					var objectCreationExpression = (ObjectCreationExpressionSyntax)declaratorSyntax.Initializer.Value;
					if (objectCreationExpression.Initializer != null)
						defaultValue = string.Join(", ", objectCreationExpression.Initializer.Expressions.Select(x => x.ToString()));
					else if (objectCreationExpression.ArgumentList != null)
						defaultValue = objectCreationExpression.ArgumentList.Arguments.ToString();

					if (string.IsNullOrWhiteSpace(defaultValue))
						defaultValue = "(empty)";
				}
				else if (valueKind == SyntaxKind.TrueLiteralExpression || valueKind == SyntaxKind.FalseLiteralExpression)
					defaultValue = bool.FalseString;
				else if (valueKind == SyntaxKind.ArrayInitializerExpression)
					defaultValue = ((InitializerExpressionSyntax)declaratorSyntax.Initializer.Value).Expressions.ToString();
				else if (valueKind == SyntaxKind.DefaultExpression)
				{
					if (defaultValue.Contains('<') && defaultValue.Contains('>'))
						defaultValue = "(empty)";
				}
				else if (valueKind == SyntaxKind.ArrayCreationExpression)
				{
					var arrayCreationExpression = (ArrayCreationExpressionSyntax)declaratorSyntax.Initializer.Value;
					if (arrayCreationExpression.Initializer != null)
						defaultValue = string.Join(", ", arrayCreationExpression.Initializer.Expressions.Select(x => x.ToString()));

					if (string.IsNullOrWhiteSpace(defaultValue))
						defaultValue = "(empty)";
				}
				else if (valueKind == SyntaxKind.ImplicitArrayCreationExpression)
				{
					var arrayCreationExpression = (ImplicitArrayCreationExpressionSyntax)declaratorSyntax.Initializer.Value;
					defaultValue = string.Join(", ", arrayCreationExpression.Initializer.Expressions.Select(x => x.ToString()));

					if (string.IsNullOrWhiteSpace(defaultValue))
						defaultValue = "(empty)";
				}
				else if (valueKind != SyntaxKind.StringLiteralExpression
				         && valueKind != SyntaxKind.NumericLiteralExpression
				         && valueKind != SyntaxKind.NullLiteralExpression
				         && valueKind != SyntaxKind.InvocationExpression
				         && valueKind != SyntaxKind.UnaryMinusExpression)
				{
					throw new NotImplementedException($"unsupported type {valueKind}!");
				}
			}
			catch (Exception e)
			{
				Console.Error.WriteLine(e);
			}

			return defaultValue;
		}

		static IEnumerable<(string TypeName, IEnumerable<string> ClassFields)> GetTraitBaseTypes(List<TraitInfo> traitInfos, string traitInfoName)
		{
			// TODO: It would be useful to know what the `Requires` requires.
			if (traitInfoName == "TraitInfo" || traitInfoName == "Requires" || (traitInfoName.StartsWith("I") && !traitInfoName.EndsWith("Info")))
				return new[] { (traitInfoName, Enumerable.Empty<string>()) };

			var traitInfo = traitInfos.FirstOrDefault(x => x.TraitInfoName == traitInfoName);
			if (traitInfo.TraitInfoName == null)
				return Enumerable.Empty<(string, IEnumerable<string>)>();

			var result = new List<(string TypeName, IEnumerable<string> ClassFieldInfos)>
			{
				(traitInfoName, traitInfo.TraitPropertyInfos.Select(x => x.Name))
			};

			foreach (var baseType in traitInfo.BaseTypes)
				result.AddRange(GetTraitBaseTypes(traitInfos, baseType));

			return result;
		}

		static IEnumerable<(string TypeName, IEnumerable<string> ClassFields)> GetWarheadBaseTypes(List<SimpleClassInfo> classInfos, string classInfoName)
		{
			var classInfo = classInfos.FirstOrDefault(x => x.InfoName == classInfoName);
			if (classInfo.InfoName == null)
				return Enumerable.Empty<(string, IEnumerable<string>)>();

			var result = new List<(string TypeName, IEnumerable<string> ClassFieldInfos)>
			{
				(classInfoName, classInfo.PropertyInfos.Select(x => x.Name))
			};

			foreach (var inheritedType in classInfo.InheritedTypes)
				result.AddRange(GetWarheadBaseTypes(classInfos, inheritedType));

			return result;
		}
	}
}

// RoslynCodeParserSymbolProvider
// DecompileBinarySymbolProvider
// StaticSourceSymbolProvider
