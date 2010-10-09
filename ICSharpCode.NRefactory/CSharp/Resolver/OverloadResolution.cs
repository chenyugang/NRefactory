﻿// Copyright (c) 2010 AlphaSierraPapa for the SharpDevelop Team (for details please see \doc\copyright.txt)
// This code is distributed under MIT X11 license (for details please see \doc\license.txt)

using System;
using System.Collections.Generic;
using System.Linq;
using ICSharpCode.NRefactory.TypeSystem;
using ICSharpCode.NRefactory.TypeSystem.Implementation;
using ICSharpCode.NRefactory.Util;

namespace ICSharpCode.NRefactory.CSharp.Resolver
{
	/// <summary>
	/// Description of OverloadResolution.
	/// </summary>
	public class OverloadResolution
	{
		sealed class Candidate
		{
			public readonly IParameterizedMember Member;
			
			/// <summary>
			/// Returns the normal form candidate, if this is an expanded candidate.
			/// </summary>
			public readonly bool IsExpandedForm;
			
			public readonly IType[] ParameterTypes;
			
			/// <summary>
			/// argument index -> parameter index; -1 for arguments that could not be mapped
			/// </summary>
			public int[] ArgumentToParameterMap;
			
			public OverloadResolutionErrors Errors;
			public int ErrorCount;
			
			public bool HasUnmappedOptionalParameters;
			
			public IList<IParameter> Parameters { get { return Member.Parameters; } }
			
			public bool IsGenericMethod {
				get {
					IMethod method = Member as IMethod;
					return method != null && method.TypeParameters.Count > 0;
				}
			}
			
			public int ArgumentsPassedToParamsArray {
				get {
					int count = 0;
					if (IsExpandedForm) {
						int paramsParameterIndex = this.Parameters.Count - 1;
						foreach (int parameterIndex in ArgumentToParameterMap) {
							if (parameterIndex == paramsParameterIndex)
								count++;
						}
					}
					return count;
				}
			}
			
			public Candidate(IParameterizedMember member, bool isExpanded)
			{
				this.Member = member;
				this.IsExpandedForm = isExpanded;
				this.ParameterTypes = new IType[member.Parameters.Count];
			}
			
			public void AddError(OverloadResolutionErrors newError)
			{
				this.Errors |= newError;
				this.ErrorCount++;
			}
		}
		
		ITypeResolveContext context;
		ResolveResult[] arguments;
		string[] argumentNames;
		Conversions conversions;
		List<Candidate> candidates = new List<Candidate>();
		Candidate bestCandidate;
		Candidate bestCandidateAmbiguousWith;
		
		#region Constructor
		public OverloadResolution(ITypeResolveContext context, ResolveResult[] arguments, string[] argumentNames = null)
		{
			if (context == null)
				throw new ArgumentNullException("context");
			if (arguments == null)
				throw new ArgumentNullException("arguments");
			if (argumentNames == null)
				argumentNames = new string[arguments.Length];
			else if (argumentNames.Length != arguments.Length)
				throw new ArgumentException("argumentsNames.Length must be equal to arguments.Length");
			this.context = context;
			this.arguments = arguments;
			this.argumentNames = argumentNames;
			this.conversions = new Conversions(context);
		}
		#endregion
		
		#region AddCandidate
		public OverloadResolutionErrors AddCandidate(IParameterizedMember member)
		{
			if (member == null)
				throw new ArgumentNullException("member");
			
			Candidate c = new Candidate(member, false);
			if (CalculateCandidate(c)) {
				candidates.Add(c);
			}
			
			if (member.Parameters.Count > 0 && member.Parameters[member.Parameters.Count - 1].IsParams) {
				Candidate expandedCandidate = new Candidate(member, true);
				// consider expanded form only if it isn't obviously wrong
				if (CalculateCandidate(expandedCandidate)) {
					candidates.Add(expandedCandidate);
					
					if (expandedCandidate.ErrorCount < c.ErrorCount)
						return expandedCandidate.Errors;
				}
			}
			return c.Errors;
		}
		
		/// <summary>
		/// Calculates applicability etc. for the candidate.
		/// </summary>
		/// <returns>True if the calculation was successful, false if the candidate should be removed without reporting an error</returns>
		bool CalculateCandidate(Candidate candidate)
		{
			if (!ResolveParameterTypes(candidate))
				return false;
			MapCorrespondingParameters(candidate);
			CheckApplicability(candidate);
			ConsiderIfNewCandidateIsBest(candidate);
			return true;
		}
		
		bool ResolveParameterTypes(Candidate candidate)
		{
			for (int i = 0; i < candidate.Parameters.Count; i++) {
				IType type = candidate.Parameters[i].Type.Resolve(context);
				if (candidate.IsExpandedForm && i == candidate.Parameters.Count - 1) {
					ArrayType arrayType = type as ArrayType;
					if (arrayType != null && arrayType.Dimensions == 1)
						type = arrayType;
					else
						return false; // error: cannot unpack params-array. abort considering the expanded form for this candidate
				}
				candidate.ParameterTypes[i] = type;
			}
			return true;
		}
		#endregion
		
		#region MapCorrespondingParameters
		void MapCorrespondingParameters(Candidate candidate)
		{
			// C# 4.0 spec: §7.5.1.1 Corresponding parameters
			candidate.ArgumentToParameterMap = new int[arguments.Length];
			for (int i = 0; i < arguments.Length; i++) {
				candidate.ArgumentToParameterMap[i] = -1;
				if (argumentNames[i] == null) {
					// positional argument
					if (i < candidate.ParameterTypes.Length) {
						candidate.ArgumentToParameterMap[i] = i;
					} else if (candidate.IsExpandedForm) {
						candidate.ArgumentToParameterMap[i] = candidate.ParameterTypes.Length - 1;
					} else {
						candidate.AddError(OverloadResolutionErrors.TooManyPositionalArguments);
					}
				} else {
					// named argument
					for (int j = 0; j < candidate.Member.Parameters.Count; j++) {
						if (argumentNames[i] == candidate.Member.Parameters[j].Name) {
							candidate.ArgumentToParameterMap[i] = j;
						}
					}
					if (candidate.ArgumentToParameterMap[i] < 0)
						candidate.AddError(OverloadResolutionErrors.NoParameterFoundForNamedArgument);
				}
			}
		}
		#endregion
		
		#region CheckApplicability
		void CheckApplicability(Candidate candidate)
		{
			// C# 4.0 spec: §7.5.3.1 Applicable function member
			
			// Test whether parameters were mapped the correct number of arguments:
			int[] argumentCountPerParameter = new int[candidate.ParameterTypes.Length];
			foreach (int parameterIndex in candidate.ArgumentToParameterMap) {
				if (parameterIndex >= 0)
					argumentCountPerParameter[parameterIndex]++;
			}
			for (int i = 0; i < argumentCountPerParameter.Length; i++) {
				if (candidate.IsExpandedForm && i == argumentCountPerParameter.Length - 1)
					continue; // any number of arguments is fine for the params-array
				if (argumentCountPerParameter[i] == 0) {
					if (candidate.Parameters[i].IsOptional)
						candidate.HasUnmappedOptionalParameters = true;
					else
						candidate.AddError(OverloadResolutionErrors.MissingArgumentForRequiredParameter);
				} else if (argumentCountPerParameter[i] > 1) {
					candidate.AddError(OverloadResolutionErrors.MultipleArgumentsForSingleParameter);
				}
			}
			
			// Test whether argument passing mode matches the parameter passing mode
			for (int i = 0; i < arguments.Length; i++) {
				int parameterIndex = candidate.ArgumentToParameterMap[i];
				if (parameterIndex < 0) continue;
				
				ByReferenceResolveResult brrr = arguments[i] as ByReferenceResolveResult;
				if (brrr != null) {
					if ((brrr.IsOut && !candidate.Parameters[parameterIndex].IsOut) || (brrr.IsRef && !candidate.Parameters[parameterIndex].IsRef))
						candidate.AddError(OverloadResolutionErrors.ParameterPassingModeMismatch);
				} else {
					if (candidate.Parameters[parameterIndex].IsOut || candidate.Parameters[parameterIndex].IsRef)
						candidate.AddError(OverloadResolutionErrors.ParameterPassingModeMismatch);
				}
				if (!conversions.ImplicitConversion(arguments[i], candidate.ParameterTypes[parameterIndex]))
					candidate.AddError(OverloadResolutionErrors.ArgumentTypeMismatch);
			}
		}
		#endregion
		
		#region BetterFunctionMember
		/// <summary>
		/// Returns 1 if c1 is better than c2; 2 if c2 is better than c1; or 0 if neither is better.
		/// </summary>
		int BetterFunctionMember(Candidate c1, Candidate c2)
		{
			if (c1.ErrorCount < c2.ErrorCount) return 1;
			if (c1.ErrorCount > c2.ErrorCount) return 2;
			
			// C# 4.0 spec: §7.5.3.2 Better function member
			bool c1IsBetter = false;
			bool c2IsBetter = false;
			for (int i = 0; i < arguments.Length; i++) {
				int p1 = c1.ArgumentToParameterMap[i];
				int p2 = c2.ArgumentToParameterMap[i];
				if (p1 >= 0 && p2 < 0) {
					c1IsBetter = true;
				} else if (p1 < 0 && p2 >= 0) {
					c2IsBetter = true;
				} else {
					switch (conversions.BetterConversion(arguments[i], c1.ParameterTypes[p1], c2.ParameterTypes[p2])) {
						case 1:
							c1IsBetter = true;
							break;
						case 2:
							c2IsBetter = true;
							break;
					}
				}
			}
			if (c1IsBetter && !c2IsBetter)
				return 1;
			if (!c1IsBetter && c2IsBetter)
				return 2;
			if (!c1IsBetter && !c2IsBetter) {
				// we need the tie-breaking rules
				
				// non-generic methods are better
				if (!c1.IsGenericMethod && c2.IsGenericMethod)
					return 1;
				else if (c1.IsGenericMethod && !c2.IsGenericMethod)
					return 2;
				
				// non-expanded members are better
				if (!c1.IsExpandedForm && c2.IsExpandedForm)
					return 1;
				else if (c1.IsExpandedForm && !c2.IsExpandedForm)
					return 2;
				
				// prefer the member with less arguments mapped to the params-array
				int r = c1.ArgumentsPassedToParamsArray.CompareTo(c2.ArgumentsPassedToParamsArray);
				if (r < 0) return 1;
				else if (r > 0) return 2;
				
				// prefer the member where no default values need to be substituted
				if (!c1.HasUnmappedOptionalParameters && c2.HasUnmappedOptionalParameters)
					return 1;
				else if (c1.HasUnmappedOptionalParameters && !c2.HasUnmappedOptionalParameters)
					return 2;
				
				// compare the formal parameters
				r = MoreSpecificFormalParameters(c1, c2);
				if (r != 0)
					return r;
				
				// prefer non-lifted operators
				if (!(c1.Member is ILiftedOperator) && (c2.Member is ILiftedOperator))
					return 1;
				if ((c1.Member is ILiftedOperator) && !(c2.Member is ILiftedOperator))
					return 2;
			}
			return 0;
		}
		
		/// <summary>
		/// Implement this interface to give overload resolution a hint that the member represents a lifted operator,
		/// which is used in the tie-breaking rules.
		/// </summary>
		public interface ILiftedOperator : IParameterizedMember
		{
		}
		
		int MoreSpecificFormalParameters(Candidate c1, Candidate c2)
		{
			// prefer the member with more formal parmeters (in case both have different number of optional parameters)
			int r = c1.Parameters.Count.CompareTo(c2.Parameters.Count);
			if (r > 0) return 1;
			else if (r < 0) return 2;
			
			bool c1IsBetter = false;
			bool c2IsBetter = false;
			for (int i = 0; i < c1.Parameters.Count; i++) {
				switch (MoreSpecificFormalParameter(c1.Parameters[i].Type.Resolve(context), c2.Parameters[i].Type.Resolve(context))) {
					case 1:
						c1IsBetter = true;
						break;
					case 2:
						c2IsBetter = true;
						break;
				}
			}
			if (c1IsBetter && !c2IsBetter)
				return 1;
			if (!c1IsBetter && c2IsBetter)
				return 2;
			return 0;
		}
		
		static int MoreSpecificFormalParameter(IType t1, IType t2)
		{
			if ((t1 is ITypeParameter) && !(t2 is ITypeParameter))
				return 2;
			if ((t2 is ITypeParameter) && !(t1 is ITypeParameter))
				return 1;
			
			ParameterizedType p1 = t1 as ParameterizedType;
			ParameterizedType p2 = t2 as ParameterizedType;
			if (p1 != null && p2 != null && p1.TypeParameterCount == p2.TypeParameterCount) {
				bool t1IsBetter = false;
				bool t2IsBetter = false;
				for (int i = 0; i < p1.TypeParameterCount; i++) {
					switch (MoreSpecificFormalParameter(p1.TypeArguments[i], p2.TypeArguments[i])) {
						case 1:
							t1IsBetter = true;
							break;
						case 2:
							t2IsBetter = true;
							break;
					}
				}
				if (t1IsBetter && !t2IsBetter)
					return 1;
				if (!t1IsBetter && t2IsBetter)
					return 2;
			}
			TypeWithElementType tew1 = t1 as TypeWithElementType;
			TypeWithElementType tew2 = t2 as TypeWithElementType;
			if (tew1 != null && tew2 != null) {
				return MoreSpecificFormalParameter(tew1.ElementType, tew2.ElementType);
			}
			return 0;
		}
		#endregion
		
		#region ConsiderIfNewCandidateIsBest
		void ConsiderIfNewCandidateIsBest(Candidate candidate)
		{
			if (bestCandidate == null) {
				bestCandidate = candidate;
			} else {
				switch (BetterFunctionMember(candidate, bestCandidate)) {
					case 0:
						bestCandidateAmbiguousWith = candidate;
						break;
					case 1:
						bestCandidate = candidate;
						bestCandidateAmbiguousWith = null;
						break;
						// case 2: best candidate stays best
				}
			}
		}
		#endregion
		
		public IParameterizedMember BestCandidate {
			get { return bestCandidate.Member; }
		}
		
		public OverloadResolutionErrors BestCandidateErrors {
			get {
				OverloadResolutionErrors err = bestCandidate.Errors;
				if (bestCandidateAmbiguousWith != null)
					err |= OverloadResolutionErrors.AmbiguousMatch;
				return err;
			}
		}
		
		public IParameterizedMember BestCandidateAmbiguousWith {
			get { return bestCandidateAmbiguousWith.Member; }
		}
	}
}