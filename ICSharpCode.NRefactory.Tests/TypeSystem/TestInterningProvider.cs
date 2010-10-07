﻿
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

using ICSharpCode.NRefactory.TypeSystem.Implementation;
using NUnit.Framework;

namespace ICSharpCode.NRefactory.TypeSystem
{
	[TestFixture]
	public class TestInterningProvider : IInterningProvider
	{
		sealed class ReferenceComparer : IEqualityComparer<object>
		{
			public new bool Equals(object a, object b)
			{
				return ReferenceEquals(a, b);
			}
			
			public int GetHashCode(object obj)
			{
				return RuntimeHelpers.GetHashCode(obj);
			}
		}
		
		sealed class InterningComparer : IEqualityComparer<ISupportsInterning>
		{
			public bool Equals(ISupportsInterning x, ISupportsInterning y)
			{
				return x.EqualsForInterning(y);
			}
			
			public int GetHashCode(ISupportsInterning obj)
			{
				return obj.GetHashCodeForInterning();
			}
		}
		
		sealed class ListComparer : IEqualityComparer<IEnumerable<object>>
		{
			public bool Equals(IEnumerable<object> a, IEnumerable<object> b)
			{
				if (a.GetType() != b.GetType())
					return false;
				return Enumerable.SequenceEqual(a, b, new ReferenceComparer());
			}
			
			public int GetHashCode(IEnumerable<object> obj)
			{
				int hashCode = obj.GetType().GetHashCode();
				unchecked {
					foreach (object o in obj) {
						hashCode *= 27;
						hashCode += RuntimeHelpers.GetHashCode(o);
					}
				}
				return hashCode;
			}
		}
		
		HashSet<object> uniqueObjectsPreIntern = new HashSet<object>(new ReferenceComparer());
		HashSet<object> uniqueObjectsPostIntern = new HashSet<object>(new ReferenceComparer());
		Dictionary<object, object> byValueDict = new Dictionary<object, object>();
		Dictionary<ISupportsInterning, ISupportsInterning> supportsInternDict = new Dictionary<ISupportsInterning, ISupportsInterning>(new InterningComparer());
		Dictionary<IEnumerable<object>, IEnumerable<object>> listDict = new Dictionary<IEnumerable<object>, IEnumerable<object>>(new ListComparer());
		
		public T Intern<T>(T obj) where T : class
		{
			if (obj == null)
				return null;
			uniqueObjectsPreIntern.Add(obj);
			ISupportsInterning s = obj as ISupportsInterning;
			if (s != null) {
				s.PrepareForInterning(this);
				ISupportsInterning output;
				if (supportsInternDict.TryGetValue(s, out output))
					obj = (T)output;
				else
					supportsInternDict.Add(s, s);
			} else if (obj is string || obj is IType || obj is bool || obj is int) {
				object output;
				if (byValueDict.TryGetValue(obj, out output))
					obj = (T)output;
				else
					byValueDict.Add(obj, obj);
			}
			uniqueObjectsPostIntern.Add(obj);
			return obj;
		}
		
		public IList<T> InternList<T>(IList<T> list) where T : class
		{
			if (list == null)
				return null;
			uniqueObjectsPreIntern.Add(list);
			for (int i = 0; i < list.Count; i++) {
				T oldItem = list[i];
				T newItem = Intern(oldItem);
				if (oldItem != newItem) {
					if (list.IsReadOnly)
						list = new List<T>(list);
					list[i] = newItem;
				}
			}
			IEnumerable<object> output;
			if (listDict.TryGetValue(list, out output))
				list = (IList<T>)output;
			else
				listDict.Add(list, list);
			uniqueObjectsPostIntern.Add(list);
			return list;
		}
		
		void Run(ITypeDefinition c)
		{
			foreach (ITypeDefinition n in c.InnerClasses) {
				Run(n);
			}
			Intern(c.Name);
			foreach (IProperty p in c.Properties) {
				Intern(p.Name);
				Intern(p.ReturnType);
				InternList(p.Parameters);
				InternList(p.Attributes);
			}
			foreach (IMethod m in c.Methods) {
				Intern(m.Name);
				Intern(m.ReturnType);
				InternList(m.Parameters);
				InternList(m.Attributes);
			}
			foreach (IField f in c.Fields) {
				Intern(f.Name);
				Intern(f.ReturnType);
				Intern(f.ConstantValue);
				InternList(f.Attributes);
			}
			foreach (IEvent e in c.Events) {
				Intern(e.Name);
				Intern(e.ReturnType);
				InternList(e.Attributes);
			}
		}
		
		[Test]
		public void PrintStatistics()
		{
			foreach (var c in CecilLoaderTests.Mscorlib.GetClasses()) {
				Run(c);
			}
			
			var stats =
				from obj in uniqueObjectsPreIntern
				group 1 by obj.GetType() into g
				join g2 in (from obj in uniqueObjectsPostIntern group 1 by obj.GetType()) on g.Key equals g2.Key
				orderby g.Key.FullName
				select new { Type = g.Key, PreCount = g.Count(), PostCount = g2.Count() };
			foreach (var element in stats) {
				Console.WriteLine(element.Type + ": " + element.PostCount + "/" + element.PreCount);
			}
		}
	}
}