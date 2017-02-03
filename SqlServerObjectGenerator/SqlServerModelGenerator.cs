﻿using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using LinqToDB.SqlProvider;
using Std.Tools.Data.CodeModel;
using Std.Tools.Data.Metadata;
using Std.Tools.Data.Metadata.Model;
using Std.Utility;
using Std.Utility.Linq;
using AssociationType = Std.Tools.Data.Metadata.Model.AssociationType;
using Attribute = Std.Tools.Data.Metadata.Model.Attribute;
using Class = Std.Tools.Data.Metadata.Model.Class;
using ForeignKey = Std.Tools.Data.Metadata.Model.ForeignKey;
using Namespace = Std.Tools.Data.Metadata.Model.Namespace;
using Procedure = Std.Tools.Data.Metadata.Model.Procedure;
using SchemaModel = Std.Tools.Data.Metadata.Model.SchemaModel;
using Table = Std.Tools.Data.Metadata.Model.Table;
// ReSharper disable LocalizableElement

namespace Std.Tools.Data
{
	public class SqlServerModelGenerator : CodeWriter
	{	
		public bool UsePascalCasing { get; set; }

		public bool GenerateDataContextClass { get; set; }

		public string BaseDataContextClass { get; set; }

		public bool PluralizeClassNames { get; set; }

		public bool SingularizeClassNames { get; set; }

		public bool PluralizeDataContextPropertyNames { get; set; }

		public bool SingularizeDataContextPropertyNames { get; set; }

		public string DatabaseName { get; set; }

		public string DataContextName { get; set; }

		public string DataNamespace { get; set; }

		public string PocoNamespace { get; set; }

		public string AdapterNamespace { get; set; }

		public string BaseEntityClass { get; set; }

		public string OneToManyDataObjectAssociationType { get; set; }

		public string OneToManyPocoAssociationType { get; set; }

		public string OwnerToInclude { get; set; }

		public string[] DatabaseQuote { get; set; }

		public bool RenderField { get; set; }

		public bool RenderBackReferences { get; set; }

		public bool RenderForeignKeys { get; set; }

		public string DataObjectSuffix { get; set; }

		public string PocoSuffix { get; set; }

		public string AdapterSuffix { get; set; }

		public List<string> IncludeTables { get; set; }

		public bool GeneratePocos { get; set; }

		public bool GenerateDataObjects { get; set; }

		public bool GenerateAdapters { get; set; }

		public bool AddAutoGeneratedHeader { get; set; }

		public bool AdjustColumnTypes { get; set; }

		public string IncludeTablePattern { get; set; }

		private Regex _includeTableMatcher;

		private SchemaModel _model;

		private List<Table> _tables;
		private List<Class> _classes;
		private List<Procedure> _procedures;
		private List<Namespace> _namespaces;
		private readonly ISqlBuilderProvider _sqlBuilderProvider;

		private Results _results;

		private List<Table> _targetTables;

		public SqlServerModelGenerator(ISqlBuilderProvider sqlBuilderProvider)
		{
			_sqlBuilderProvider = sqlBuilderProvider;
			IncludeTables = null;
			BaseDataContextClass = "DbManager";
			DatabaseName = null;
			DataContextName = null;
			DataNamespace = null;
			PocoNamespace = null;
			BaseEntityClass = null;
			OneToManyDataObjectAssociationType = "IEnumerable<{0}>";
			OneToManyPocoAssociationType = "List<{0}>";
			RenderForeignKeys = false;
			RenderBackReferences = true;
			RenderField = false;
			DatabaseQuote = null;
			OwnerToInclude = null;
			GenerateDataContextClass = true;
			AddAutoGeneratedHeader = false;
			DataObjectSuffix = "Data";
			PocoSuffix = "";
			AdapterSuffix = "Adapter";
			AdjustColumnTypes = true;

			SingularizeDataContextPropertyNames = false;
			PluralizeDataContextPropertyNames = true;
			SingularizeClassNames = true;
			PluralizeClassNames = false;
		}

		private string SetCasing(string name)
		{
			if (UsePascalCasing)
			{
				return name?.ToPascalCase();
			}

			return name;
		}

		public Results Generate(SchemaModel model)
		{
			_model = model;

			var flattener = new ModelFlattener();
			model.Accept(flattener);

			_tables = flattener.Tables;
			_classes = flattener.Classes;
			_procedures = flattener.Procedures;
			_namespaces = flattener.Namespaces;

			foreach (var t in _tables)
			{
				t.TableName = SetCasing(t.TableName);
				t.Name = SetCasing(t.Name);

				var className = RemoveSuffix(SetCasing(t.DataObjectClassName ?? t.TableName), "_t");

				var propName = SetCasing(t.DataContextPropertyName ?? className);

				if (PluralizeDataContextPropertyNames || SingularizeDataContextPropertyNames)
				{
					var newName = PluralizeDataContextPropertyNames ? ToPlural(propName) : ToSingular(propName);

					t.DataContextPropertyName = newName;
				}
				else
				{
					t.DataContextPropertyName = SetCasing(propName);
				}

				if (PluralizeClassNames || SingularizeClassNames)
				{
					className = PluralizeClassNames ? ToPlural(className) : ToSingular(className);
				}

				t.DataObjectClassName = className;
				if (DataObjectSuffix != null)
				{
					t.DataObjectClassName += DataObjectSuffix;
				}

				className = SetCasing(RemoveSuffix(t.PocoClassName ?? t.TableName, "_t"));

				t.PocoClassName = className;
				if (!PocoSuffix.IsNullOrEmpty())
				{
					t.PocoClassName += PocoSuffix;
				}
			}

			if (AdapterNamespace.IsNullOrEmpty())
			{
				AdapterNamespace = DataNamespace;
			}

			DataContextName = DataContextName?.Trim();

			_results = new Results();

			if (string.IsNullOrEmpty(DataContextName))
			{
				DataContextName = "DataContext";
			}

			var allTables = (from t in _tables orderby t.TableName select t).ToList();

			_targetTables = allTables;

			if (IncludeTables != null &&
				IncludeTables.Count > 0)
			{
				_targetTables = allTables.Where(t => IncludeTables.Contains(t.OriginalTableName)).ToList();
			}

			if (!IncludeTablePattern.IsNullOrEmpty())
			{
				if (_targetTables == null)
				{
					_targetTables = new List<Table>();
				}

				_includeTableMatcher = new Regex(IncludeTablePattern,
					RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

				foreach (var table in allTables)
				{
					if (_includeTableMatcher.IsMatch(table.OriginalTableName) &&
						!_targetTables.Contains(table))
					{
						_targetTables.Add(table);
					}
				}
			}

			if (GeneratePocos || GenerateAdapters)
			{
				BuildPocoModel();
			}

			if (GenerateDataObjects)
			{
				GenerateDataObjectModels();
			}

			return _results;

		}

		private void WriteTableProperty(string name, string pname)
		{
			WriteLine("public Table<{0}> {1} {{ get {{ return this.GetTable<{0}>(); }} }}", RemoveSuffix(name, "_t"),
				RemoveSuffix(pname, "_t"));
		}

		private string RemoveSuffix(string value, string suffix)
		{
			return value;
			//if (value.EndsWith(suffix.ToLowerInvariant()) ||
			//	value.EndsWith(suffix.ToUpperInvariant()))
			//{
			//	value = value.Substring(0, value.Length - suffix.Length);
			//}

			//return value;
		}

		private void RenderColumn(Column column, CodeType codeType)
		{
			WriteSummary(column.Description);

			if (codeType == CodeType.DataObject)
			{
				RenderAttributes(column);
			}

			Write($"public {column.Type} {column.MemberName}");

			if (RenderField)
			{
				Write(";");
			}
			else
			{
				Write(" { get; set; }");
			}

			WriteLine("");
			WriteLine("");
		}

		private void RenderForeignKey(ForeignKey key, CodeType codeType)
		{
			//if (!RenderForeignKeys)
			//{
			//	return;
			//}

			//if (codeType == CodeType.DataObject)
			//{
			//	WriteComment(" " + key.KeyName);
			//	WriteLine("[Association(ThisKey=\"{0}\", OtherKey=\"{1}\", CanBeNull={2})]",
			//		string.Join(", ", (from c in key.ThisColumns select c.ColumnName).ToArray()),
			//		string.Join(", ", (from c in key.OtherColumns select c.MemberName).ToArray()),
			//		key.CanBeNull ? "true" : "false");

			//	if (key.Attributes.Count > 0)
			//	{
			//		WriteAttribute(string.Join(", ", key.Attributes.DistinctBy(a => a.Name).ToArray()));
			//		WriteAttributeLine();
			//	}
			//}

			//Write("public ");

			//var otherTableName = RemoveSuffix(key.OtherTable.DataObjectClassName, "_t");

			//if (key.AssociationType == AssociationType.OneToMany)
			//{
			//	Write(codeType == CodeType.DataObject
			//		? OneToManyDataObjectAssociationType
			//		: OneToManyPocoAssociationType, otherTableName);
			//}
			//else
			//{
			//	Write(otherTableName);
			//}

			//Write(" ");
			//Write(otherTableName);
			//if (key.AssociationType == AssociationType.OneToMany)
			//{
			//	if (otherTableName.EndsWith("s"))
			//	{
			//		Write("es");
			//	}
			//	else
			//	{
			//		Write("s");
			//	}
			//}
			//else if (key.OtherTable.DataObjectClassName == otherTableName)
			//{
			//	Write("Other");
			//}

			//if (RenderField)
			//{
			//	WriteLine(";");
			//}
			//else
			//{
			//	WriteLine(" { get; set; }");
			//}
		}

		private string GetFkName(ForeignKey key)
		{
			var isBackReference = key.KeyName.Contains("BackReference");
			var keyName = key.KeyName.Replace("FK_", "").Replace("_T", "").Replace("_BackReference", "");

			var parts = keyName.Split('_');
			if (parts.Length == 1)
			{
				return key.KeyName;
			}

			if (isBackReference)
			{
				return parts[0];
			}

			return parts[1];
		}

		private void BuildPocoModel()
		{
			var tableClassMap = new Dictionary<Table, ClassDefinition>();

			foreach (var table in _targetTables)
			{
				var defn = BuildClass(table);
				tableClassMap.Add(table, defn);
			}

			if (RenderForeignKeys)
			{
				foreach (var table in _targetTables)
				{
					if (table.ForeignKeys.Count == 0)
					{
						continue;
					}

					var defn = tableClassMap[table];

					foreach (var key in table.ForeignKeys.Values)
					{
						var myColumn = key.ThisColumns.FirstOrDefault();
						if (myColumn == null)
						{
							continue;
						}

						var mbr = new MemberDefinition
						{
							OriginalColumnName = GetFkName(key)
						};

						var ombr = defn.Members.FirstOrDefault(m => m.OriginalColumnName == myColumn.MemberName);
						if (ombr != null)
						{
							ombr.Skip = true;
						}

						defn.Members.Add(mbr);
						var mbrType = RemoveSuffix(key.OtherTable.DataObjectClassName, "_t");
						if (key.AssociationType == AssociationType.OneToMany ||
							key.AssociationType == AssociationType.Auto)
						{
							mbr.Name = PluralizeAssociationName(RemoveSuffix(mbr.OriginalColumnName, "_ID"));
							mbr.Type = OneToManyPocoAssociationType.FormatWith(mbrType);
							mbr.IsContainer = true;
							mbr.ElementTypeName = mbrType;
						}
						else
						{
							mbr.Name = SingularizeAssociationName(RemoveSuffix(mbr.OriginalColumnName, "_ID"));
							mbr.Type = mbrType;
						}

						mbr.Comment = key.AssociationType.ToString();
						var isBackReference = key.KeyName.Contains("BackReference");
						if (isBackReference)
						{
							mbr.Comment += ", backref";
						}

						if (mbr.Name.IsNullOrEmpty())
						{
							mbr.Name = "NO_NAME";
							mbr.Comment += ", '" + key.KeyName + "'";
						}
					}
				}
			}

			foreach (var defn in tableClassMap.Values)
			{
				if (GeneratePocos)
				{
					GeneratePocoModel(defn);
				}
				
				if (GenerateAdapters)
				{
					GenerateAdapter(defn);
				}
			}
		}

		private void GeneratePocoModel(ClassDefinition defn)
		{
			Reset();

			Console.WriteLine($"Generating POCO {defn.Name}");

			WriteAutoGeneratedHeader();

			RenderUsings(_pocoUsings);

			WriteBeginNamespace(PocoNamespace);

			WriteAttribute("Serializable");
			WriteAttributeLine();
			WriteBeginClass(defn.Name + (PocoSuffix ?? ""));

			foreach (var member in defn.Members)
			{
				if (member.Skip)
				{
					continue;
				}
				if (!member.Comment.IsNullOrEmpty())
				{
					WriteComment(member.Comment);
				}
				WriteLine("{2}public {0} {1} {{ get; set; }}", 
					member.Type, 
					member.Name.IsNullOrEmpty() ? "NO_NAME" : member.Name,
					member.Skip ? "// " : "");
				WriteLine("");
			}

			WriteEndClass();
			WriteEndNamespace();

			var model = new Code
			{
				Type = CodeType.Poco,
				Name = defn.Name,
				Content = CodeOutput
			};

			_results.Models.Add(model);

			Reset();
		}

		private void GenerateAdapter(ClassDefinition defn)
		{
			Reset();

			WriteAutoGeneratedHeader();

			RenderUsings(_adapterUsings);

			WriteBeginNamespace(AdapterNamespace);

			WriteBeginClass(defn.AdapterName);

			GenerateFromAdapter(defn);
			WriteLine("");
			GenerateToAdapter(defn);

			WriteEndClass();
			WriteEndNamespace();

			var model = new Code
			{
				Type = CodeType.Adapter,
				Name = defn.AdapterName,
				Content = CodeOutput
			};

			_results.Models.Add(model);

			Reset();
		}

		private void GenerateFromAdapter(ClassDefinition defn)
		{
			WriteLine("public static {0}{1} From{0}{2}(this {0}{2} source)", defn.Name, DataObjectSuffix, PocoSuffix);
			WriteLine("{");
			PushIndent();

			WriteLine("if (source == null)");
			WriteLine("{");
			PushIndent();
			WriteLine("return null;");
			PopIndent();
			WriteLine("}");

			WriteLine("var result = new {0}{1}", defn.Name, DataObjectSuffix);
			WriteLine("{");
			PushIndent();


			foreach (var member in defn.Members)
			{
				if (member.Skip ||
					member.Name.IsNullOrEmpty())
				{
					continue;
				}

				if (member.IsContainer)
				{
					WriteLine("{0} = source.{0}.From{1}(),", member.Name, member.ElementTypeName);
				}
				else
				{
					WriteLine("{0} = source.{0},", member.Name);
				}
			}

			PopIndent();
			WriteLine("};");
			WriteLine("");
			WriteLine("return result;");
			PopIndent();
			WriteLine("}");

			WriteLine("");
			WriteLine("private static readonly {0}{1}[] _empty{0}{1}Objects = new {0}{1}[0];", defn.Name, DataObjectSuffix);
			WriteLine("public static IEnumerable<{0}{1}> From{0}{2}(this IEnumerable<{0}{2}> source)", defn.Name, DataObjectSuffix, PocoSuffix);
			WriteLine("{");
			PushIndent();

			WriteLine("if (source == null)");
			WriteLine("{");
			PushIndent();
			WriteLine("return _empty{0}{1}Objects;", defn.Name, DataObjectSuffix);
			PopIndent();
			WriteLine("}");

			WriteLine("");

			WriteLine("var result = source.Select(From{0}{1});", defn.Name, DataObjectSuffix);
			WriteLine("return result;");
			PopIndent();
			WriteLine("}");

			WriteLine("");
		}

		private void GenerateToAdapter(ClassDefinition defn)
		{
			Console.WriteLine("Generating adapter " + defn.AdapterName);

			WriteLine("public static {0}{1} To{0}{1}(this {0}{2} source)", defn.Name, PocoSuffix, DataObjectSuffix);
			WriteLine("{");
			PushIndent();

			WriteLine("if (source == null)");
			WriteLine("{");
			PushIndent();
			WriteLine("return null;");
			PopIndent();
			WriteLine("}");

			WriteLine("var result = new {0}{1}", defn.Name, PocoSuffix);
			WriteLine("{");
			PushIndent();


			foreach (var member in defn.Members)
			{
				if (member.Skip ||
					member.Name.IsNullOrEmpty())
				{
					continue;
				}

				WriteLine("{0} = source.{0},", member.Name);
			}

			PopIndent();
			WriteLine("};");
			WriteLine("");
			WriteLine("return result;");
			PopIndent();
			WriteLine("}");

			WriteLine("");
			WriteLine("private static readonly {0}{1}[] _empty{0}{1}Pocos = new {0}{1}[0];", defn.Name, PocoSuffix);
			WriteLine("public static IEnumerable<{0}{1}> To{0}{1}(this IEnumerable<{0}{2}> source)", defn.Name, PocoSuffix, DataObjectSuffix);
			WriteLine("{");
			PushIndent();

			WriteLine("if (source == null)");
			WriteLine("{");
			PushIndent();
			WriteLine("return _empty{0}{1}Pocos;", defn.Name, PocoSuffix);
			PopIndent();
			WriteLine("}");

			WriteLine("");

			WriteLine("var result = source.Select(To{0}{1});", defn.Name, PocoSuffix);
			WriteLine("return result;");
			PopIndent();
			WriteLine("}");

			WriteLine("");
		}

		class MemberDefinition
		{
			public string Name { get; set; }
			public string Type { get; set; }
			public string OriginalColumnName { get; set; }
			public string Comment { get; set; }
			public bool Skip { get; set; }
			public bool IsContainer { get; set; }
			public string ElementTypeName { get; set; }
		}

		class ClassDefinition
		{
			public string Name { get; set; }
			public string AdapterName { get; set; }
			public List<MemberDefinition> Members { get; private set; }
			public string OriginalTableName { get; set; }

			public ClassDefinition()
			{
				Members = new List<MemberDefinition>();
			}
		}

		private ClassDefinition BuildClass(Table table)
		{
			var defn = new ClassDefinition
			{
				Name = table.PocoClassName,
				//Name = RemoveSuffix(table.DataObjectClassName, "_t"),
				OriginalTableName = table.TableName,
				AdapterName = table.TableName.Replace("_", "") + (AdapterSuffix ?? "")
			};

			foreach (var column in table.Columns.Values)
			{
				var memberName = column.MemberName;
				if (AdjustColumnTypes)
				{
					//if (column.MemberName.EndsWith("_ID"))
					//{
					//	memberName = RemoveSuffix(column.MemberName, "_ID") + "Id";
					//}
					//else
					//{
					//	memberName = Regex.Replace(column.MemberName, "^F[0-9]+_", "");
					//	memberName = RemoveSuffix(memberName, "_VC");
					//	memberName = RemoveSuffix(memberName, "_BT");
					//	memberName = RemoveSuffix(memberName, "_DT");
					//	memberName = RemoveSuffix(memberName, "_IN");
					//	memberName = RemoveSuffix(memberName, "_TX");
					//	memberName = RemoveSuffix(memberName, "_DC");
					//	memberName = RemoveSuffix(memberName, "_SM");
					//	memberName = RemoveSuffix(memberName, "_TI");
					//	memberName = RemoveSuffix(memberName, "_CH");
					//	memberName = RemoveSuffix(memberName, "_BI");
					//	memberName = RemoveSuffix(memberName, "_MX");
					//	memberName = RemoveSuffix(memberName, "_PC");
					//}

					memberName = memberName.Replace("_", "");
				}

				var member = new MemberDefinition
				{
					Name = memberName,
					Type = GetColumnType(memberName, column),
					OriginalColumnName = column.MemberName,
					Skip = column.IsIdentity
				};

				defn.Members.Add(member);
			}

			return defn;
		}

		private readonly Regex _amountMatcher = new Regex(@"Amount\d*\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
		private readonly Regex _dateMatcher = new Regex(@"(\bDate)|(Date\d*\b)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

		private string GetColumnType(string columnName, Column col)
		{
			if (!AdjustColumnTypes)
			{
				return col.Type;
			}

			if (col.Type == "byte")
			{
				return "int";
			}

			if (col.Type == "short")
			{
				return "int";
			}

			var isAmount = _amountMatcher.IsMatch(columnName);
			if (isAmount && col.Type == "string")
			{
				if (col.IsNullable)
				{
					return "decimal?";
				}

				return "decimal?";
			}

			var isDate = _dateMatcher.IsMatch(columnName);
			if (isDate && col.Type == "string")
			{
				if (col.IsNullable)
				{
					return "DateTime?";
				}

				return "DateTime?";
			}

			return col.Type;
		}

		private void RenderTable(Table table, bool renderForeignKeys, CodeType codeType)
		{
			WriteSummary(table.Description);

			if (codeType == CodeType.DataObject && 
				table.IsView)
			{
				WriteComment(" View");
			}

			RenderAttributes(table);

			table.DataObjectClassName = RemoveSuffix(table.DataObjectClassName, "_t");

			Console.WriteLine("Generating data object " + table.DataObjectClassName);

			WriteBeginClass(table.DataObjectClassName, table.BaseClassName);

			if (AdjustColumnTypes &&
				!table.ColumnsCleaned)
			{
				table.ColumnsCleaned = true;

				foreach (var column in table.Columns.Values)
				{
					if (column.MemberName.EndsWith("_ID"))
					{
						column.MemberName = RemoveSuffix(column.MemberName, "_ID") + "Id";
					}

					column.MemberName = column.MemberName.Replace("_", "");
				}
			}

			if (table.Columns.Count > 0)
			{
				foreach (var c in table.OrderedColumns)
				{
					RenderColumn(c, codeType);
				}
			}

			if (RenderForeignKeys && table.ForeignKeys.Count > 0)
			{
				foreach (var key in table.ForeignKeys.Values)
				{
					WriteLine("");
					RenderForeignKey(key, codeType);
				}
			}

			WriteEndClass();
		}

		private void RenderAttributeBody(Attribute a)
		{
			Write(a.Name);

			if (a.Parameters.Count > 0)
			{
				Write("(");

				for (var i = 0; i < a.Parameters.Count; i++)
				{
					if (i > 0)
					{
						if (a.Parameters[i - 1].All(c => c == ' '))
						{
							Write(" ");
						}
						else
						{
							Write(", ");
						}
					}
					Write(a.Parameters[i]);
				}

				Write(")");
			}
		}

		private void RenderAttributes(IAttributeProvider provider)
		{
			var attrs = provider.Attributes.Where(a => !a.IsSeparated).ToList();

			if (attrs.Count > 0)
			{
				Write("[");

				var first = provider.Attributes.First();
				foreach(var attr in provider.Attributes)
				{
					if (attr != first)
					{
						Write(", ");
					}
					RenderAttributeBody(attr);
				}

				WriteLine("]");
			}

			attrs = provider.Attributes.Where(a => a.IsSeparated).ToList();

			foreach (var attr in attrs)
			{
				Write("[");
				RenderAttributeBody(attr);
				WriteLine("]");
			}
		}

		private readonly List<string> _dataUsings = new List<string>
		{
			"System",
			"System.Data",
			"LinqToDB.Data",
			"LinqToDB.Common"
		};

		private readonly List<string> _pocoUsings = new List<string>
		{
			"System",
		};


		private readonly List<string> _adapterUsings = new List<string>
		{
			"System",
			"System.Collections.Generic"
		};

		private readonly List<string> _procedureUsings = new List<string>
		{
			"System",
			"System.Collections.Generic",
			"System.Data",
			"LinqToDB.Data",
			"LinqToDB.Common"
		};

		private void RenderUsings(List<string> usings)
		{
			var q =
				from ns in usings.Distinct()
				group ns by ns.Split('.')[0];

			var groups =
				(from ns in q where ns.Key == "System" select ns).Concat
					(from ns in q where ns.Key != "System" orderby ns.Key select ns);

			foreach (var gr in groups)
			{
				foreach (var ns in from s in gr orderby s select s)
				{
					WriteUsing(ns);
				}

				WriteLine("");
			}
		}

		private void BeforeGenerateModel()
		{
			
		}

		private void BeforeGenerateDataModel()
		{
			_dataUsings.Add("System.Runtime.Serialization");

			foreach (var t in _tableIndex.Values)
			{
				t.Attributes.Add(new Attribute {Name = "Serializable"});
			}

			foreach (var t in _tableIndex.Values)
			{
				foreach (var c in t.Columns.Values)
				{
					if (c.Type == "string" && c.Length > 0)
					{
						c.Attributes.Add(new Attribute("MaxLength", c.Length.ToString()));
					}

					if (!c.IsNullable)
					{
						c.Attributes.Add(new Attribute("Required"));
					}
				}
			}
		}

		private void BeforeGenerateDataContext()
		{
			
		}

		private void AfterGenerateDataContext()
		{
			
		}

		private void AfterGenerateDataModel()
		{
		}

		private void AfterGenerateModel()
		{			
		}

		private void WriteAutoGeneratedHeader()
		{
			if (!AddAutoGeneratedHeader)
			{
				return;
			}

			WriteComment("---------------------------------------------------------------------------------------------------");
			WriteComment(" <auto-generated>");
			WriteComment("    This code was auto generated.");
			WriteComment("    Changes to this file may cause incorrect behavior and will be lost if the code is regenerated.");
			WriteComment(" </auto-generated>");
			WriteComment("---------------------------------------------------------------------------------------------------");
		}

		private void GenerateDataContext(bool isMainPart, bool renderingProcedures = false)
		{
			if (!GenerateDataContextClass)
			{
				return;
			}

			var generatingProcedures = renderingProcedures &&
				!isMainPart &&
				_procedures.NotNullOrEmpty();

			Console.WriteLine($"Generating data context {DataContextName}");

			BeforeGenerateDataContext();
	
			WriteAutoGeneratedHeader();
			RenderUsings(_dataUsings);
			WriteBeginNamespace(DataNamespace);
			WriteBeginClass(DataContextName, 
				generatingProcedures
					? BaseDataContextClass
					: null,
				ispartial: renderingProcedures);

			if (generatingProcedures)
			{
				GenerateProcedures();
			}
			else
			{
				foreach (var t in _targetTables)
				{
					WriteTableProperty(t.DataObjectClassName, t.DataContextPropertyName ?? t.DataObjectClassName);
				}
			}

			WriteEndClass();
			WriteEndNamespace();

			AfterGenerateDataContext();

			if (generatingProcedures)
			{
				_results.DataContextProcedurePart = CodeOutput;
			}
			else
			{
				_results.DataContextMainPart = CodeOutput;
			}
		}

		private void GenerateTableModel(Table table)
		{
			Reset();
			BeforeGenerateModel();

			WriteAutoGeneratedHeader();

			RenderUsings(_dataUsings);

			WriteBeginNamespace(DataNamespace);

			RenderTable(table, RenderForeignKeys, CodeType.DataObject);

			WriteEndNamespace();

			AfterGenerateDataModel();

			var model = new Code
			{
				Type = CodeType.DataObject,
				Name = table.DataObjectClassName,
				Content = CodeOutput
			};

			_results.Models.Add(model);

			Reset();
		}

		private void GenerateDataObjectModels()
		{
			BeforeGenerateDataModel();

			Reset();
			GenerateDataContext(isMainPart: true, renderingProcedures: true);
			Reset();
			GenerateDataContext(isMainPart: false, renderingProcedures: true);
			Reset();

			foreach (var table in _targetTables)
			{
				GenerateTableModel(table);
			}

			AfterGenerateDataModel();
		}

		private void GenerateProcedures()
		{
			if (_procedures.Count == 0)
			{
				return;
			}

			foreach (var proc in _procedures.Where(p => p.ResultException != null))
			{
				Console.WriteLine($"Proc {proc.Name} failed with error: {proc.ResultException.Message}");
			}

			var allProcs = _procedures.Where(p =>p.IsLoaded || p.IsFunction && !p.IsTableFunction || p.IsTableFunction && p.ResultException != null)
				.ToList();

			var storedProcedures = allProcs.Where(p => !p.IsFunction && p.ResultException == null).ToList();
			var tableFunctions = allProcs.Where(p => p.IsTableFunction && p.ResultException == null).ToList();

			var targetProcs = tableFunctions.Concat(storedProcedures).ToList();

			var lastProc = targetProcs.LastOrDefault();
			foreach (var proc in targetProcs)
			{
				if (proc.IsFunction)
				{
					continue;
				}

				var inputParameters = proc.ProcParameters.Where(pp => pp.IsIn && !pp.IsResult).ToList();
				var outputParameters = proc.ProcParameters.Where(pp => pp.IsOut && !pp.IsResult).ToList();

				var args = proc.ProcParameters.Where(pp => pp.IsIn)
					.Concat(proc.ProcParameters.Where(pp => pp.IsOut))
					.ToList();

				var builder = _sqlBuilderProvider.SqlBuilder;

				var procName =
					builder.BuildTableName(
						new StringBuilder(),
						(string) builder.Convert(DatabaseName, ConvertType.NameToDatabase),
						(string) builder.Convert(proc.SchemaName, ConvertType.NameToOwner),
						(string) builder.Convert(proc.ProcedureName, ConvertType.NameToQueryTable)
					).ToString();

				procName = $"\"{procName.Replace("\"", "\\\"")}\"";

				Console.WriteLine($"Generating procedure {procName}");

				Write($"public {proc.Type} Exec{proc.MethodName}(");

				RenderParameters(args, includeType: true);

				WriteLine(")");
				OpenScope();

				if (proc.IsTableFunction)
				{
					RenderTableFunction(proc);
				}
				else
				{
					RenderProcedure(proc, procName, inputParameters, outputParameters);
				}

				CloseScope();

				if (proc != lastProc)
				{
					WriteLine();
				}
			}
		}

		private void RenderProcedure(Procedure proc,
		                             string procName,
		                             List<Parameter> inputParameters,
		                             List<Parameter> outputParameters)
		{
			var resultName = "result";
			var retNo = 0;

			while (proc.ProcParameters.Any(pp => pp.ParameterName == resultName))
			{
				resultName = "result" + ++retNo;
			}

			var hasOut = outputParameters.Any(pr => pr.IsOut);
			var prefix = $"var {resultName} = ";

			if (proc.ResultTable == null)
			{
				Write($"{prefix}ExecuteProc(\"{proc.ProcedureName}\"");
				PushIndent();
			}
			else
			{
				if (proc.ResultTable.OrderedColumns.Any(c => c.IsDuplicateOrEmpty))
				{
					WriteLine($"{prefix}QueryProc(dataReader =>");
					PushIndent();
					WriteLine($"new {proc.ResultTable.TypeName}");
					OpenScope();

					var n = 0;
					var first = proc.ResultTable.OrderedColumns.FirstOrDefault();
					foreach (var c in proc.ResultTable.OrderedColumns)
					{
						Write($"{c.MemberName} = Converter.ChangeTypeTo<{c.Type}>(dataReader.GetValue({n++}), MappingSchema)");
						WriteLine(first != c ? "," : "");
					}

					CloseScope("},");
					Write(procName);
				}
				else
				{
					Write($"{prefix}QueryProc<{proc.ResultTable.TypeName}>({procName}");
					PushIndent();
				}
			}

			if (inputParameters.Count > 0)
			{
				WriteLine(",");
			}

			var lastInput = inputParameters.LastOrDefault();

			foreach (var arg in inputParameters)
			{
				Write ($"new DataParameter(\"{arg.SchemaName}\", {arg.ParameterName}, {"DataType." + arg.DataType})");

				if (arg.IsOut)
				{
					Write(" { Direction = " + (arg.IsIn
						? "ParameterDirection.InputOutput"
						: "ParameterDirection.Output"));

					if (arg.Size != null &&
						arg.Size.Value != 0)
					{
						Write(", Size = " + arg.Size.Value);
					}

					Write(" }");
				}
				WriteLine(arg != lastInput ? "," : "");
			}

			PopIndent();
			WriteLine(");");

			if (hasOut)
			{
				WriteLine();

				foreach (var pr in outputParameters)//proc.ProcParameters.Where(_ => _.IsOut))
				{
					WriteLine($"{pr.ParameterName} = Converter.ChangeTypeTo<{pr.ParameterType}>(((IDbDataParameter)Command.Parameters[\"{pr.SchemaName}\"]).Value);");
				}
			}

			WriteLine();
			WriteLine("return " + resultName + ";");
		}

		private void RenderParameters(List<Parameter> args, bool includeType)
		{
			var first = args.FirstOrDefault();
			foreach (var arg in args)
			{
				if (arg != first)
				{
					Write(", ");
				}

				if (arg.IsOut)
				{
					Write("out ");
				}
				else if (arg.IsIn &&
					arg.IsOut)
				{
					Write("ref ");
				}

				if (includeType)
				{
					Write(arg.ParameterType + " ");
				}
				Write(arg.ParameterName);
			}
		}

		private void RenderTableFunction(Procedure proc)
		{
			Write($"return GetTable<{proc.ResultTable.TypeName}>((MethodInfo)MethodBase.GetCurrentMethod()");
			RenderParameters(proc.ProcParameters, includeType: false);
			Write(");");
		}

		private string PluralizeAssociationName(string assoc)
		{
			return ToPlural(assoc);
			//return assoc + "s";
		}

		private string SingularizeAssociationName(string assoc)
		{
			return ToSingular(assoc);
			//return assoc;
		}

		private void BeforeLoadMetadata()
		{			
		}

		private readonly Dictionary<string, Table> _tableIndex = new Dictionary<string, Table>();

		private string ToPlural(string str)
		{
			var word = GetLastWord(str);
			var newWord = Plurals.ToPlural(word);

			if (word != newWord)
			{
				if (char.IsUpper(word[0]))
				{
					newWord = char.ToUpper(newWord[0]) + newWord.Substring(1, newWord.Length - 1);
				}

				return word == str ? newWord : str.Substring(0, str.Length - word.Length) + newWord;
			}

			return str;
		}

		private string ToSingular(string str)
		{
			if (str.ToLowerInvariant().EndsWith("ess"))
			{
				return str;
			}

			var word = GetLastWord(str);
			var newWord = Plurals.ToSingular(word);

			if (word != newWord)
			{
				if (char.IsUpper(word[0]))
				{
					newWord = char.ToUpper(newWord[0]) + newWord.Substring(1, newWord.Length - 1);
				}

				return word == str ? newWord : str.Substring(0, str.Length - word.Length) + newWord;
			}

			return str;
		}

		private string GetLastWord(string word)
		{
			if (string.IsNullOrEmpty(word))
			{
				return word;
			}

			var len = word.Length;
			var n = len - 1;

			if (char.IsLower(word[n]))
			{
				for (; n > 0 && char.IsLower(word[n]); n--)
				{
					;
				}
			}
			else
			{
				for (; n > 0 && char.IsUpper(word[n]); n--)
				{
					;
				}
				if (char.IsLower(word[n]))
				{
					n++;
				}
			}

			return n > 0 ? word.Substring(n) : word;
		}
	}
}