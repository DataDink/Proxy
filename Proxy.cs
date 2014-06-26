using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

namespace Proxy
{
    // ReSharper disable StaticFieldInGenericType
    public abstract class Proxy<T> where T : class
    {
        #region Static Cache (NOTE: This is per T)
        /// <summary>
        /// The dynamic proxy type 
        /// </summary>
        protected static readonly Type ProxyType;
        /// <summary>
        /// The type of T
        /// </summary>
        protected static readonly Type Interface;

        private static readonly Dictionary<int, MemberInfo> MemberIndex;
        /// <summary>
        /// Looks up the MethodInfo for type T by index
        /// </summary>
        public static MemberInfo Lookup(int index) { return MemberIndex[index]; }

        private readonly static AssemblyBuilder Assembly;
        /// <summary>
        /// Saves the underlying generated assembly to disk
        /// </summary>
        public static void Save() { Assembly.Save(Assembly.GetName().Name + ".dll"); }

        static Proxy()
        {
            Interface = GetInterfaceType();
            MemberIndex = GenerateMemberIndex();
            Assembly = GenerateAssembly();
            ProxyType = GenerateProxy();
        }

        private static Type GetInterfaceType()
        {
            var type = typeof (T);
            if (!type.IsInterface) throw new InvalidOperationException(string.Format("{0} is not an interface", type.Name));
            return type;
        }

        private static Dictionary<int, MemberInfo> GenerateMemberIndex()
        {
            return Interface.GetInterfaces().Concat(new[] {Interface})
                .SelectMany(i => i.GetMembers())
                .Select((member, index) => new {member, index})
                .ToDictionary(m => m.index, m => m.member);
        } 
        #endregion

        /// <summary>
        /// The underlying instance being proxied or null
        /// </summary>
        protected T Target { get; private set; }
        /// <summary>
        /// The proxy instance
        /// </summary>
        protected T Instance { get; private set; }

        protected Proxy() { Instance = (T)Activator.CreateInstance(ProxyType, this); }
        protected Proxy(T target) : this() { Target = target; }

        /// <summary>
        /// Invokes the call handlers for this proxy
        /// </summary>
        public object Trigger(MethodInfo method, object[] parameters)
        {
            BeforeCall(method, parameters);
            var result = OnCall(method, parameters);
            AfterCall(method, parameters, result);
            return result;
        }

        /// <summary>
        /// When overridden called prior to OnCall
        /// </summary>
        protected virtual void BeforeCall(MethodInfo method, object[] parameters) { }
        /// <summary>
        /// When overridden called after OnCall
        /// </summary>
        protected virtual void AfterCall(MethodInfo method, object[] parameters, object result) { }
        /// <summary>
        /// When overridden should handle invoking the MethodInfo on Target if any
        /// </summary>
        protected virtual object OnCall(MethodInfo method, object[] parameters)
        {
            if (Target == null)
                return method.ReturnType.IsValueType ? Activator.CreateInstance(method.ReturnType) : null;
            return method.Invoke(Target, parameters);
        }

        #region Emit Generation (

        private static AssemblyBuilder GenerateAssembly()
        {
            var rootname = new AssemblyName(Interface.Name + "Proxy_" + Guid.NewGuid());
            return AppDomain.CurrentDomain.DefineDynamicAssembly(rootname, AssemblyBuilderAccess.RunAndSave);
        }

        private static Type GenerateProxy()
        {
            var nspace = Assembly.GetName().Name;
            var module = Assembly.DefineDynamicModule(nspace, nspace + ".dll");
            var builder = module.DefineType(nspace + ".Proxy", TypeAttributes.Class, typeof(object), new[] { typeof(T) });
            var proxy = builder.DefineField("_proxy", typeof(Proxy<T>), FieldAttributes.Private);

            ConfigureConstructor(builder, proxy);
            ImplementInterface(builder, proxy);
            return builder.CreateType();
        }

        private static void ConfigureConstructor(TypeBuilder builder, FieldInfo proxy)
        {
            var ctor = builder.DefineConstructor(MethodAttributes.Public, CallingConventions.HasThis, new[] { typeof(Proxy<T>) });
            var ctorBase = typeof(object).GetConstructor(new Type[0]);
            var encoder = ctor.GetILGenerator(); // Creates a constructor that sets the _proxy field
            encoder.Emit(OpCodes.Ldarg_0); encoder.Emit(OpCodes.Call, ctorBase); encoder.Emit(OpCodes.Ldarg_0);
            encoder.Emit(OpCodes.Ldarg_1); encoder.Emit(OpCodes.Stfld, proxy); encoder.Emit(OpCodes.Ret);
        }

        private static void ImplementInterface(TypeBuilder builder, FieldInfo proxy = null)
        {
            var methods = MemberIndex.Values.OfType<MethodInfo>().Where(m => !m.IsSpecialName).ToList();
            var properties = MemberIndex.Values.OfType<PropertyInfo>().ToList();

            methods.ForEach(m => ConfigureMethod(builder, proxy, m));
            properties.ForEach(p => ConfigureProperty(builder, proxy, p));
        }

        private static void ConfigureProperty(TypeBuilder builder, FieldInfo proxy, PropertyInfo info)
        {
            var property = builder.DefineProperty(
                string.Format("Explicit_{0}.{1}", Guid.NewGuid(), info.Name),
                info.Attributes,
                CallingConventions.HasThis,
                info.PropertyType,
                new Type[0]);
            if (info.CanRead) property.SetGetMethod(ConfigureMethod(builder, proxy, info.GetGetMethod()));
            if (info.CanWrite) property.SetSetMethod(ConfigureMethod(builder, proxy, info.GetSetMethod()));
        }

        // Blog: http://markernet.blogspot.com/2014/06/interface-proxy-with-emit.html
        // Github: https://github.com/DataDink/Proxy
        private static MethodBuilder ConfigureMethod(TypeBuilder builder, FieldInfo proxy, MethodInfo info)
        {
            var index = MemberIndex.First(m => m.Value == info).Key;
            var method = builder.DefineMethod(
                string.Format("Explicit_{0}.{1}", Guid.NewGuid(), info.Name),
                MethodAttributes.Private | MethodAttributes.Virtual | MethodAttributes.HideBySig | MethodAttributes.Final |
                MethodAttributes.NewSlot | (info.Attributes & MethodAttributes.SpecialName),
                CallingConventions.HasThis,
                info.ReturnType,
                info.GetParameters().Select(p => p.ParameterType).ToArray());
            builder.DefineMethodOverride(method, info);
            var encoder = method.GetILGenerator();
            ConfigureMethodBody(encoder, proxy, info, index);
            return method;
        }

        // Blog: http://markernet.blogspot.com/2014/06/interface-proxy-with-emit.html
        // Github: https://github.com/DataDink/Proxy
        private static void ConfigureMethodBody(ILGenerator encoder, FieldInfo proxy, MethodInfo method, int index)
        {
            var returnsVoid = method.ReturnType == typeof(void);
            var returnsValue = !returnsVoid && method.ReturnType.IsValueType;
            var lookup = typeof(Proxy<T>).GetMethod("Lookup", BindingFlags.Public | BindingFlags.Static);
            var interceptor = proxy.FieldType.GetMethod("Trigger", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var arguments = method.GetParameters().Select(p => p.ParameterType).ToArray();

            encoder.DeclareLocal(returnsVoid ? typeof(object) : method.ReturnType); // return value (local 0)
            encoder.DeclareLocal(typeof(object[])); // parameters (local 1)

            encoder.Emit(OpCodes.Ldarg_0); encoder.Emit(OpCodes.Ldfld, proxy); // this._proxy
            encoder.Emit(OpCodes.Ldc_I4_S, index); encoder.Emit(OpCodes.Call, lookup); // lookup methodinfo by const index
            encoder.Emit(OpCodes.Ldc_I4_S, arguments.Length); encoder.Emit(OpCodes.Newarr, typeof(object)); encoder.Emit(OpCodes.Stloc_1); // create parameter array

            for (var i = 0; i < arguments.Length; i++) { // load up parameter array values
                var argument = arguments[i];
                encoder.Emit(OpCodes.Ldloc_1); ConfigureLdc(encoder, i); // get array item at index
                ConfigureLdarg(encoder, i + 1); // get argument (index + 1)
                if (argument.IsValueType) encoder.Emit(OpCodes.Box, argument); // convert to object if needed
                encoder.Emit(OpCodes.Stelem_Ref); // push to array item
            }

            encoder.Emit(OpCodes.Ldloc_1); // get array
            encoder.Emit(OpCodes.Callvirt, interceptor); // pass stack to method

            if (returnsValue) encoder.Emit(OpCodes.Unbox_Any, method.ReturnType); // un-object a value
            else if (!returnsVoid) encoder.Emit(OpCodes.Castclass, method.ReturnType); // cast to return type
            else  encoder.Emit(OpCodes.Pop); // discard return value
            encoder.Emit(OpCodes.Ret);
        }

        private static readonly OpCode[] Ldargs = new[] { OpCodes.Ldarg_0, OpCodes.Ldarg_1, OpCodes.Ldarg_2, OpCodes.Ldarg_3 };
        private static void ConfigureLdarg(ILGenerator encoder, int index)
        {
            if (index >= Ldargs.Length) encoder.Emit(OpCodes.Ldarg_S, (short)(index));
            else encoder.Emit(Ldargs[index]);
        }

        private static readonly OpCode[] Ldcs = new[] { OpCodes.Ldc_I4_0, OpCodes.Ldc_I4_1, OpCodes.Ldc_I4_2, OpCodes.Ldc_I4_3, OpCodes.Ldc_I4_4, OpCodes.Ldc_I4_5, OpCodes.Ldc_I4_6, OpCodes.Ldc_I4_7, OpCodes.Ldc_I4_8 };
        private static void ConfigureLdc(ILGenerator encoder, int index)
        {
            if (index >= Ldcs.Length) encoder.Emit(OpCodes.Ldc_I4, index);
            else encoder.Emit(Ldcs[index]);
        }
        #endregion
    }
}
