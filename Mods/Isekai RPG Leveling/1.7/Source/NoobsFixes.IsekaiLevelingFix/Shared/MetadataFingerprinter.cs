using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;

namespace NoobsFixes.IsekaiLevelingFix
{
	/// <summary>
	/// Fingerprints a method body read straight from an assembly file. Metadata tokens are replaced
	/// by the names they point at, so a rebuild that merely renumbers tokens (which every upstream
	/// update does) keeps the same fingerprint, while any real change to the method's code gives a
	/// new one.
	///
	/// This reads the file instead of using runtime reflection on purpose: the 1.7 runtime re-binds
	/// Isekai's 1.6-era references loosely, so reflection could name things differently inside the
	/// game than in the offline tool that records the expected values. Shared with that tool, so
	/// nothing in this file may reference RimWorld or Harmony.
	/// </summary>
	internal sealed class MetadataFingerprinter : IDisposable
	{
		private static readonly OpCode[] OneByte = new OpCode[0x100];
		private static readonly OpCode[] TwoByte = new OpCode[0x100];
		private static readonly bool[] OneByteDefined = new bool[0x100];
		private static readonly bool[] TwoByteDefined = new bool[0x100];

		static MetadataFingerprinter()
		{
			foreach (FieldInfo f in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
			{
				if (!(f.GetValue(null) is OpCode op)) continue;
				ushort v = unchecked((ushort)op.Value);
				if (op.Size == 1)
				{
					OneByte[v & 0xFF] = op;
					OneByteDefined[v & 0xFF] = true;
				}
				else
				{
					TwoByte[v & 0xFF] = op;
					TwoByteDefined[v & 0xFF] = true;
				}
			}
		}

		private readonly PEReader pe;
		private readonly MetadataReader md;
		private readonly Dictionary<string, TypeDefinitionHandle> typesByName = new Dictionary<string, TypeDefinitionHandle>();

		public MetadataFingerprinter(byte[] image)
		{
			pe = new PEReader(new MemoryStream(image, false));
			md = pe.GetMetadataReader();
			foreach (TypeDefinitionHandle h in md.TypeDefinitions)
			{
				typesByName[FullName(h)] = h;
			}
		}

		public void Dispose() => pe.Dispose();

		/// <summary>Fingerprint of the anchor's method body, or false with a reason.</summary>
		public bool TryFingerprint(Anchor anchor, out string fingerprint, out string error)
		{
			fingerprint = null;
			if (!TryDescribe(anchor, out string text, out error)) return false;
			fingerprint = Hash(text);
			return true;
		}

		/// <summary>
		/// The normalized, human-readable IL the fingerprint is computed from. The tool can dump
		/// this to diff what upstream changed in a method.
		/// </summary>
		public bool TryDescribe(Anchor anchor, out string text, out string error)
		{
			text = null;
			error = null;
			if (!typesByName.TryGetValue(anchor.TypeName, out TypeDefinitionHandle th))
			{
				error = "type " + anchor.TypeName + " not found";
				return false;
			}

			MethodDefinitionHandle found = default;
			int matches = 0;
			foreach (MethodDefinitionHandle mh in md.GetTypeDefinition(th).GetMethods())
			{
				MethodDefinition m = md.GetMethodDefinition(mh);
				if (md.GetString(m.Name) != anchor.MethodName) continue;
				if (anchor.ParamCount >= 0 && ParamCount(m.Signature) != anchor.ParamCount) continue;
				found = mh;
				matches++;
			}
			if (matches == 0)
			{
				error = "method " + anchor.Display + " not found";
				return false;
			}
			if (matches > 1)
			{
				error = matches + " overloads of " + anchor.Display + " exist; the anchor needs a parameter count";
				return false;
			}

			MethodDefinition def = md.GetMethodDefinition(found);
			if (def.RelativeVirtualAddress == 0)
			{
				error = anchor.Display + " has no IL body";
				return false;
			}
			text = Describe(pe.GetMethodBody(def.RelativeVirtualAddress));
			return true;
		}

		private string Describe(MethodBodyBlock body)
		{
			var sb = new StringBuilder(2048);
			sb.Append("locals ").Append(LocalCount(body)).Append('\n');
			foreach (ExceptionRegion r in body.ExceptionRegions)
			{
				sb.Append("eh ").Append(r.Kind.ToString())
					.Append(' ').Append(r.TryOffset).Append('+').Append(r.TryLength)
					.Append(' ').Append(r.HandlerOffset).Append('+').Append(r.HandlerLength);
				if (r.Kind == ExceptionRegionKind.Catch) sb.Append(' ').Append(EntityName(r.CatchType));
				sb.Append('\n');
			}

			BlobReader il = body.GetILReader();
			while (il.RemainingBytes > 0)
			{
				byte b = il.ReadByte();
				OpCode op;
				if (b == 0xFE)
				{
					byte b2 = il.ReadByte();
					if (!TwoByteDefined[b2]) { sb.Append("?fe").Append(b2).Append('\n'); break; }
					op = TwoByte[b2];
				}
				else
				{
					if (!OneByteDefined[b]) { sb.Append("?").Append(b).Append('\n'); break; }
					op = OneByte[b];
				}

				sb.Append(op.Name);
				switch (op.OperandType)
				{
					case OperandType.InlineNone:
						break;
					case OperandType.ShortInlineBrTarget:
					case OperandType.ShortInlineI:
					case OperandType.ShortInlineVar:
						sb.Append(' ').Append(il.ReadByte());
						break;
					case OperandType.InlineVar:
						sb.Append(' ').Append(il.ReadUInt16());
						break;
					case OperandType.InlineI:
					case OperandType.InlineBrTarget:
						sb.Append(' ').Append(il.ReadInt32());
						break;
					case OperandType.ShortInlineR:
						sb.Append(' ').Append(il.ReadInt32()); // raw bits: culture-independent
						break;
					case OperandType.InlineI8:
					case OperandType.InlineR:
						sb.Append(' ').Append(il.ReadInt64()); // raw bits
						break;
					case OperandType.InlineSwitch:
						int n = il.ReadInt32();
						sb.Append(' ').Append(n);
						for (int i = 0; i < n; i++) sb.Append(',').Append(il.ReadInt32());
						break;
					case OperandType.InlineString:
						int st = il.ReadInt32();
						sb.Append(" \"").Append(md.GetUserString(MetadataTokens.UserStringHandle(st & 0x00FFFFFF))).Append('"');
						break;
					default: // InlineField, InlineMethod, InlineType, InlineTok, InlineSig
						sb.Append(' ').Append(EntityName(MetadataTokens.EntityHandle(il.ReadInt32())));
						break;
				}
				sb.Append('\n');
			}
			return sb.ToString();
		}

		private int LocalCount(MethodBodyBlock body)
		{
			if (body.LocalSignature.IsNil) return 0;
			BlobReader r = md.GetBlobReader(md.GetStandaloneSignature(body.LocalSignature).Signature);
			r.ReadSignatureHeader();
			return r.ReadCompressedInteger();
		}

		private int ParamCount(BlobHandle signature)
		{
			BlobReader r = md.GetBlobReader(signature);
			SignatureHeader h = r.ReadSignatureHeader();
			if (h.Kind != SignatureKind.Method && h.Kind != SignatureKind.Property) return -1;
			if (h.IsGeneric) r.ReadCompressedInteger();
			return r.ReadCompressedInteger();
		}

		private string EntityName(EntityHandle h)
		{
			switch (h.Kind)
			{
				case HandleKind.TypeDefinition:
					return "T:" + md.GetString(md.GetTypeDefinition((TypeDefinitionHandle)h).Name);
				case HandleKind.TypeReference:
					return "T:" + md.GetString(md.GetTypeReference((TypeReferenceHandle)h).Name);
				case HandleKind.TypeSpecification:
					return "T:" + SpecName((TypeSpecificationHandle)h);
				case HandleKind.FieldDefinition:
				{
					FieldDefinition f = md.GetFieldDefinition((FieldDefinitionHandle)h);
					return "F:" + md.GetString(md.GetTypeDefinition(f.GetDeclaringType()).Name) + "::" + md.GetString(f.Name);
				}
				case HandleKind.MethodDefinition:
				{
					MethodDefinition m = md.GetMethodDefinition((MethodDefinitionHandle)h);
					return "M:" + md.GetString(md.GetTypeDefinition(m.GetDeclaringType()).Name) + "::" + md.GetString(m.Name) + "/" + ParamCount(m.Signature);
				}
				case HandleKind.MemberReference:
				{
					MemberReference r = md.GetMemberReference((MemberReferenceHandle)h);
					string owner = ParentName(r.Parent) + "::" + md.GetString(r.Name);
					return r.GetKind() == MemberReferenceKind.Field
						? "F:" + owner
						: "M:" + owner + "/" + ParamCount(r.Signature);
				}
				case HandleKind.MethodSpecification:
					return EntityName(md.GetMethodSpecification((MethodSpecificationHandle)h).Method) + "<>";
				case HandleKind.StandaloneSignature:
					return "sig";
				default:
					return h.Kind.ToString();
			}
		}

		private string ParentName(EntityHandle parent)
		{
			switch (parent.Kind)
			{
				case HandleKind.TypeDefinition:
					return md.GetString(md.GetTypeDefinition((TypeDefinitionHandle)parent).Name);
				case HandleKind.TypeReference:
					return md.GetString(md.GetTypeReference((TypeReferenceHandle)parent).Name);
				case HandleKind.TypeSpecification:
					return SpecName((TypeSpecificationHandle)parent);
				case HandleKind.MethodDefinition:
					return md.GetString(md.GetMethodDefinition((MethodDefinitionHandle)parent).Name);
				default:
					return parent.Kind.ToString();
			}
		}

		/// <summary>Generic instantiations are named after their generic type; anything else is just "spec".</summary>
		private string SpecName(TypeSpecificationHandle h)
		{
			try
			{
				BlobReader r = md.GetBlobReader(md.GetTypeSpecification(h).Signature);
				if (r.ReadSignatureTypeCode() != SignatureTypeCode.GenericTypeInstance) return "spec";
				r.ReadSignatureTypeCode(); // class / valuetype marker
				EntityHandle generic = r.ReadTypeHandle();
				int args = r.ReadCompressedInteger();
				string name = generic.Kind == HandleKind.TypeDefinition
					? md.GetString(md.GetTypeDefinition((TypeDefinitionHandle)generic).Name)
					: generic.Kind == HandleKind.TypeReference
						? md.GetString(md.GetTypeReference((TypeReferenceHandle)generic).Name)
						: "spec";
				return name + "[" + args + "]";
			}
			catch (BadImageFormatException)
			{
				return "spec";
			}
		}

		private string FullName(TypeDefinitionHandle h)
		{
			TypeDefinition td = md.GetTypeDefinition(h);
			string name = md.GetString(td.Name);
			TypeDefinitionHandle declaring = td.GetDeclaringType();
			if (!declaring.IsNil) return FullName(declaring) + "+" + name;
			string ns = md.GetString(td.Namespace);
			return ns.Length == 0 ? name : ns + "." + name;
		}

		/// <summary>64-bit FNV-1a: deterministic on every runtime, unlike string.GetHashCode.</summary>
		public static string Hash(string text)
		{
			ulong h = 14695981039346656037UL;
			for (int i = 0; i < text.Length; i++)
			{
				h ^= text[i];
				h *= 1099511628211UL;
			}
			return h.ToString("x16");
		}
	}
}
