﻿using Microsoft.JSInterop;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;

namespace ChartJs.Blazor.Interop
{
    /// <summary>
    /// Wraps a C#-delegate to make it callable by Javascript.
    /// </summary>
    /// <typeparam name="T">The type of the delegate you want to invoke from Javascript.</typeparam>
    public class DelegateHandler<T> : IMethodHandler<T>, IDisposable
        where T : Delegate
    {
        private static readonly ParameterInfo[] s_delegateParameters;
        private static readonly bool s_delegateHasReturnValue;

        private readonly T _function;
        private readonly IList<int> _ignoredIndices;

        /// <summary>
        /// The name of the method which should be called from Javascript. In this case it's always the name of the <see cref="Invoke"/>-method.
        /// </summary>
        public string MethodName => nameof(Invoke);

        /// <summary>
        /// Keeps a reference to this object which is used to invoke the stored delegate from Javascript.
        /// </summary>
        // This property only has to be serialized by the JSRuntime where a custom converter will be used.
        [JsonIgnore]
        public DotNetObjectReference<DelegateHandler<T>> HandlerReference { get; }

        /// <summary>
        /// Gets a value indicating whether or not this delegate will return a value.
        /// </summary>
        public bool ReturnsValue => s_delegateHasReturnValue;

        /// <summary>
        /// Gets the indices of the ignored callback parameters. The parameters at these indices won't
        /// be sent to C# and won't be deserialized. These indices are defined by the
        /// <see cref="IgnoreCallbackValueAttribute"/>s on the delegate passed to this instance.
        /// </summary>
        // Since this instance will be serialized by System.Text.Json in the end, we need a public property.
        public IReadOnlyCollection<int> IgnoredIndices { get; }

        static DelegateHandler()
        {
            // https://stackoverflow.com/a/429564/10883465
            MethodInfo internalDelegateMethod = typeof(T).GetMethod("Invoke");

            s_delegateParameters = internalDelegateMethod.GetParameters();
            s_delegateHasReturnValue = internalDelegateMethod.ReturnType != typeof(void);
        }

        /// <summary>
        /// Creates a new instance of <see cref="DelegateHandler{T}"/>.
        /// </summary>
        /// <param name="function">The delegate you want to invoke from Javascript.</param>
        public DelegateHandler(T function)
        {
            _function = function ?? throw new ArgumentNullException(nameof(function));
            ParameterInfo[] parameters = _function.GetMethodInfo().GetParameters();
            _ignoredIndices = new List<int>();
            IgnoredIndices = new ReadOnlyCollection<int>(_ignoredIndices);
            for (int i = 0; i < parameters.Length; i++)
            {
                if (parameters[i].GetCustomAttribute<IgnoreCallbackValueAttribute>(false) != null)
                {
                    _ignoredIndices.Add(i);
                }
            }

            HandlerReference = DotNetObjectReference.Create(this);
        }

        /// <summary>
        /// Invokes the delegate dynamically. This method should only be called from Javascript.
        /// </summary>
        /// <param name="jsonArgs">
        /// All the arguments for the method as array of json-strings.
        /// This array can contain ANYTHING, do not trust its values.
        /// </param>
        [JSInvokable]
        public object Invoke(params string[] jsonArgs)
        {
            if (s_delegateParameters.Length != jsonArgs.Length)
                throw new ArgumentException($"The function expects {s_delegateParameters.Length} arguments but found {jsonArgs.Length}.");

            if (s_delegateParameters.Length == 0)
                return _function.DynamicInvoke(null);

            object[] invokationArgs = new object[s_delegateParameters.Length];
            for (int i = 0; i < s_delegateParameters.Length; i++)
            {
                if (_ignoredIndices.Contains(i))
                    continue;

                Type deserializeType = s_delegateParameters[i].ParameterType;
                if (deserializeType == typeof(object) ||
                    typeof(JToken).IsAssignableFrom(deserializeType))
                {
                    invokationArgs[i] = JToken.Parse(jsonArgs[i]);
                }
                else
                {
#if DEBUG
                    Console.WriteLine($"Deserializing: {jsonArgs[i]} to {deserializeType.Name}");
#endif
                    invokationArgs[i] = JsonConvert.DeserializeObject(jsonArgs[i], deserializeType, ChartJsInterop.JsonSerializerSettings);
                }
            }

            return _function.DynamicInvoke(invokationArgs);
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            HandlerReference.Dispose();
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// The <see cref="Dispose"/> method doesn't have any unmanaged resources to free BUT once this object is finalized
        /// we need to prevent any further use of the <see cref="DotNetObjectReference"/> to this object. Since the <see cref="HandlerReference"/>
        /// will only be disposed if this <see cref="DelegateHandler{T}"/> instance is disposed or when <c>dispose</c> is called from Javascript
        /// (which shouldn't happen) we HAVE to dispose the reference when this instance is finalized.
        /// </summary>
        ~DelegateHandler()
        {
            Dispose();
        }

        /// <summary>
        /// Converts a delegate of type <typeparamref name="T"/> to a <see cref="DelegateHandler{T}"/> implicitly.
        /// </summary>
        /// <param name="function"></param>
        public static implicit operator DelegateHandler<T>(T function) => new DelegateHandler<T>(function);
    }
}