#region License
// Copyright (c) .NET Foundation and contributors.
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
// http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
//
// The latest version of this file can be found at https://github.com/FluentValidation/FluentValidation
#endregion

namespace FluentValidation.Internal {
	using System;
	using System.Collections.Generic;
	using System.Linq;
	using System.Linq.Expressions;
	using System.Reflection;
	using System.Threading;
	using System.Threading.Tasks;
	using Resources;
	using Results;
	using Validators;

	/// <summary>
	/// Defines a rule associated with a property.
	/// </summary>
	public class PropertyRule<T> : IValidationRule<T> {
		private readonly List<IPropertyValidator> _validators = new List<IPropertyValidator>();
		private Func<CascadeMode> _cascadeModeThunk;
		private string _propertyDisplayName;
		private string _propertyName;
		private Func<ValidationContext<T>, bool> _condition;
		private Func<ValidationContext<T>, CancellationToken, Task<bool>> _asyncCondition;
		private List<IValidationRule<T>> _dependentRules;
		private string _displayName;
		private Func<ValidationContext<T>, string> _displayNameFactory;

		internal IRuleExecutor<T> Executor { get; set; }

		/// <summary>
		/// Condition for all validators in this rule.
		/// </summary>
		public Func<ValidationContext<T>, bool> Condition => _condition;

		/// <summary>
		/// Asynchronous condition for all validators in this rule.
		/// </summary>
		public Func<ValidationContext<T>, CancellationToken, Task<bool>> AsyncCondition => _asyncCondition;

		/// <summary>
		/// Property associated with this rule.
		/// </summary>
		public MemberInfo Member { get; }

		/// <summary>
		/// Expression that was used to create the rule.
		/// </summary>
		public LambdaExpression Expression { get; }


		/// <summary>
		/// Sets the display name for the property.
		/// </summary>
		/// <param name="name">The property's display name</param>
		public void SetDisplayName(string name) {
			_displayName = name;
			_displayNameFactory = null;
		}

		/// <summary>
		/// Sets the display name for the property using a function.
		/// </summary>
		/// <param name="factory">The function for building the display name</param>
		public void SetDisplayName(Func<IValidationContext, string> factory) {
			_displayNameFactory = factory;
			_displayName = null;
		}

		/// <summary>
		/// Rule set that this rule belongs to (if specified)
		/// </summary>
		public string[] RuleSets { get; set; }

		/// <summary>
		/// Function that will be invoked if any of the validators associated with this rule fail.
		/// </summary>
		public Action<object, IEnumerable<ValidationFailure>> OnFailure { get; set; }

		/// <summary>
		/// The current validator being configured by this rule.
		/// </summary>
		public IPropertyValidator CurrentValidator => _validators.LastOrDefault();

		/// <summary>
		/// Type of the property being validated
		/// </summary>
		public Type TypeToValidate { get; }

		/// <summary>
		/// Cascade mode for this rule.
		/// </summary>
		public CascadeMode CascadeMode {
			get => _cascadeModeThunk();
			set => _cascadeModeThunk = () => value;
		}

		/// <summary>
		/// Validators associated with this rule.
		/// </summary>
		public IEnumerable<IPropertyValidator> Validators => _validators;

		internal PropertyRule(MemberInfo member, IRuleExecutor<T> ruleExecutor, LambdaExpression expression, Func<CascadeMode> cascadeModeThunk, Type typeToValidate) {
			Member = member;
			Executor = ruleExecutor;
			Expression = expression;
			TypeToValidate = typeToValidate;
			_cascadeModeThunk = cascadeModeThunk;

			PropertyName = ValidatorOptions.Global.PropertyNameResolver(typeof(T), member, expression);
			_displayNameFactory = context => ValidatorOptions.Global.DisplayNameResolver(typeof(T), member, expression);
		}

		/// <summary>
		/// Creates a new property rule from a lambda expression.
		/// </summary>
		public static PropertyRule<T> Create<TProperty>(Expression<Func<T, TProperty>> expression) {
			return Create(expression, () => ValidatorOptions.Global.CascadeMode);
		}

		/// <summary>
		/// Creates a new property rule from a lambda expression.
		/// </summary>
		public static PropertyRule<T> Create<TProperty>(Expression<Func<T, TProperty>> expression, Func<CascadeMode> cascadeModeThunk, bool bypassCache = false) {
			var member = expression.GetMember();
			var compiled = AccessorCache<T>.GetCachedAccessor(member, expression, bypassCache);
			var executor = new RuleExecutor<T,TProperty>(compiled);
			return new PropertyRule<T>(member, executor, expression, cascadeModeThunk, typeof(TProperty));
		}

		/// <summary>
		/// Adds a validator to the rule.
		/// </summary>
		public void AddValidator(IPropertyValidator validator) {
			_validators.Add(validator);
		}

		/// <summary>
		/// Replaces a validator in this rule. Used to wrap validators.
		/// </summary>
		public void ReplaceValidator(IPropertyValidator original, IPropertyValidator newValidator) {
			var index = _validators.IndexOf(original);

			if (index > -1) {
				_validators[index] = newValidator;
			}
		}

		/// <summary>
		/// Remove a validator in this rule.
		/// </summary>
		public void RemoveValidator(IPropertyValidator original) {
			_validators.Remove(original);
		}

		/// <summary>
		/// Clear all validators from this rule.
		/// </summary>
		public void ClearValidators() {
			_validators.Clear();
		}

		/// <summary>
		/// Returns the property name for the property being validated.
		/// Returns null if it is not a property being validated (eg a method call)
		/// </summary>
		public string PropertyName {
			get => _propertyName;
			set {
				_propertyName = value;
				_propertyDisplayName = _propertyName.SplitPascalCase();
			}
		}

		/// <summary>
		/// Allows custom creation of an error message
		/// </summary>
		public Func<MessageBuilderContext, string> MessageBuilder { get; set; }

		/// <summary>
		/// Dependent rules
		/// </summary>
		public List<IValidationRule<T>> DependentRules => _dependentRules ??= new List<IValidationRule<T>>();

		/// <summary>
		/// Display name for the property.
		/// </summary>
		public string GetDisplayName(ValidationContext<T> context) =>
			_displayNameFactory?.Invoke(context) ?? _displayName ?? _propertyDisplayName;


		/// <summary>
		/// Performs validation using a validation context and returns a collection of Validation Failures.
		/// </summary>
		/// <param name="context">Validation Context</param>
		/// <returns>A collection of validation failures</returns>
		public virtual IEnumerable<ValidationFailure> Validate(ValidationContext<T> context) {
			string displayName = GetDisplayName(context);

			if (PropertyName == null && displayName == null) {
				//No name has been specified. Assume this is a model-level rule, so we should use empty string instead.
				displayName = string.Empty;
			}

			// Construct the full name of the property, taking into account overriden property names and the chain (if we're in a nested validator)
			string propertyName = context.PropertyChain.BuildPropertyName(PropertyName ?? displayName);

			// Ensure that this rule is allowed to run.
			// The validatselector has the opportunity to veto this before any of the validators execute.
			if (!context.Selector.CanExecute(this, propertyName, context)) {
				return Enumerable.Empty<ValidationFailure>();
			}

			if (_condition != null) {
				if (!_condition(context)) {
					return Enumerable.Empty<ValidationFailure>();
				}
			}

			// TODO: For FV 9, throw an exception by default if synchronous validator has async condition.
			if (_asyncCondition != null) {
				if (!_asyncCondition(context, default).GetAwaiter().GetResult()) {
					return Enumerable.Empty<ValidationFailure>();
				}
			}

			var failures = new List<ValidationFailure>();

			Executor.Execute(context, this, propertyName, failures);

			if (failures.Count > 0) {
				// Callback if there has been at least one property validator failed.
				OnFailure?.Invoke(context.InstanceToValidate, failures);
			}
			else {
				foreach (var dependentRule in DependentRules) {
					failures.AddRange(dependentRule.Validate(context));
				}
			}

			return failures;
		}

		/// <summary>
		/// Performs asynchronous validation using a validation context and returns a collection of Validation Failures.
		/// </summary>
		/// <param name="context">Validation Context</param>
		/// <param name="cancellation"></param>
		/// <returns>A collection of validation failures</returns>
		public virtual async Task<IEnumerable<ValidationFailure>> ValidateAsync(ValidationContext<T> context, CancellationToken cancellation) {
			if (!context.IsAsync()) {
				context.RootContextData["__FV_IsAsyncExecution"] = true;
			}

			string displayName = GetDisplayName(context);

			if (PropertyName == null && displayName == null) {
				//No name has been specified. Assume this is a model-level rule, so we should use empty string instead.
				displayName = string.Empty;
			}

			// Construct the full name of the property, taking into account overriden property names and the chain (if we're in a nested validator)
			string propertyName = context.PropertyChain.BuildPropertyName(PropertyName ?? displayName);

			// Ensure that this rule is allowed to run.
			// The validatselector has the opportunity to veto this before any of the validators execute.
			if (!context.Selector.CanExecute(this, propertyName, context)) {
				return Enumerable.Empty<ValidationFailure>();
			}

			if (_condition != null) {
				if (!_condition(context)) {
					return Enumerable.Empty<ValidationFailure>();
				}
			}

			if (_asyncCondition != null) {
				if (! await _asyncCondition(context, cancellation)) {
					return Enumerable.Empty<ValidationFailure>();
				}
			}

			var failures = new List<ValidationFailure>();

			await Executor.ExecuteAsync(context, this, propertyName, failures, cancellation);


			if (failures.Count > 0) {
				// Callback if there has been at least one property validator failed.
				OnFailure?.Invoke(context.InstanceToValidate, failures);
			}
			else {
				foreach (var rule in DependentRules) {
					cancellation.ThrowIfCancellationRequested();
					failures.AddRange(await rule.ValidateAsync(context, cancellation));
				}
			}

			return failures;
		}

		/// <summary>
		/// Applies a condition to the rule
		/// </summary>
		/// <param name="predicate"></param>
		/// <param name="applyConditionTo"></param>
		public void ApplyCondition(Func<ValidationContext<T>, bool> predicate, ApplyConditionTo applyConditionTo = ApplyConditionTo.AllValidators) {
			// Default behaviour for When/Unless as of v1.3 is to apply the condition to all previous validators in the chain.
			Executor.ApplyCondition(this, predicate, applyConditionTo);

			if (applyConditionTo == ApplyConditionTo.AllValidators) {
				foreach (var dependentRule in DependentRules) {
					dependentRule.ApplyCondition(predicate, applyConditionTo);
				}
			}
		}

		/// <summary>
		/// Applies the condition to the rule asynchronously
		/// </summary>
		/// <param name="predicate"></param>
		/// <param name="applyConditionTo"></param>
		public void ApplyAsyncCondition(Func<ValidationContext<T>, CancellationToken, Task<bool>> predicate, ApplyConditionTo applyConditionTo = ApplyConditionTo.AllValidators) {
			// Default behaviour for When/Unless as of v1.3 is to apply the condition to all previous validators in the chain.
			Executor.ApplyAsyncCondition(this, predicate, applyConditionTo);
			foreach (var dependentRule in DependentRules) {
				dependentRule.ApplyAsyncCondition(predicate, applyConditionTo);
			}
		}

		public void ApplySharedCondition(Func<ValidationContext<T>, bool> condition) {
			if (_condition == null) {
				_condition = condition;
			}
			else {
				var original = _condition;
				_condition = ctx => condition(ctx) && original(ctx);
			}
		}

		public void ApplySharedAsyncCondition(Func<ValidationContext<T>, CancellationToken, Task<bool>> condition) {
			if (_asyncCondition == null) {
				_asyncCondition = condition;
			}
			else {
				var original = _asyncCondition;
				_asyncCondition = async (ctx, ct) => await condition(ctx, ct) && await original(ctx, ct);
			}
		}
	}
}
