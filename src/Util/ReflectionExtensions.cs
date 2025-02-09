// using HarmonyLib;
using System;
using System.Reflection;


namespace RiverGen;

public static class ReflectionExtensions
{
     public static T GetStaticField<T>(this Type type, string fieldName)
     {
         return (T)type.GetField(fieldName, BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
     }
     
     /// <summary>
     ///     Calls a method within an instance of an object, via reflection. This can be an internal or private method within another assembly.
     /// </summary>
     /// <param name="instance">The instance to call the method from.</param>
     /// <param name="method">The name of the method to call.</param>
     /// <param name="args">The arguments to pass to the method.</param>
     public static void CallMethod(this object instance, string method, params object[] args)
     {
         instance.GetType().GetMethod(method)?.Invoke(instance, args);
     }
     
     /// <summary>
     ///     Calls a static method within a type, via reflection. This can be an internal or private method within another assembly.
     /// </summary>
     /// <param name="type">The type to call the method from.</param>
     /// <param name="method">The name of the method to call.</param>
     public static T CallStaticMethod<T>(this Type type, string method, params object[] args)
     {
         return (T) type.GetMethod(method)?.Invoke(null, args);
     }
}
