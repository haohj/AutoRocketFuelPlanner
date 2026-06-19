using System;
using System.Collections.Generic;
using System.Reflection;

namespace AutoRocketFuelPlanner
{
    internal static class ReflectionHelpers
    {
        public static IEnumerable<MemberInfo> GetMembers(Type type)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            foreach (FieldInfo field in type.GetFields(flags))
            {
                yield return field;
            }

            foreach (PropertyInfo property in type.GetProperties(flags))
            {
                if (property.GetIndexParameters().Length == 0)
                {
                    yield return property;
                }
            }
        }

        public static bool TryReadFloat(object target, MemberInfo member, out float value)
        {
            value = 0f;
            try
            {
                object raw = null;
                if (member is FieldInfo field)
                {
                    raw = field.GetValue(target);
                }
                else if (member is PropertyInfo property && property.CanRead)
                {
                    raw = property.GetValue(target, null);
                }

                if (raw == null)
                {
                    return false;
                }

                if (raw is float f)
                {
                    value = f;
                    return true;
                }

                if (raw is int i)
                {
                    value = i;
                    return true;
                }

                if (raw is double d)
                {
                    value = (float)d;
                    return true;
                }
            }
            catch
            {
                // Best effort reflection only.
            }

            return false;
        }

        public static bool TryWriteFloat(object target, MemberInfo member, float value)
        {
            try
            {
                if (member is FieldInfo field)
                {
                    if (field.FieldType == typeof(float))
                    {
                        field.SetValue(target, value);
                        return true;
                    }

                    if (field.FieldType == typeof(double))
                    {
                        field.SetValue(target, (double)value);
                        return true;
                    }

                    if (field.FieldType == typeof(int))
                    {
                        field.SetValue(target, (int)value);
                        return true;
                    }
                }
                else if (member is PropertyInfo property && property.CanWrite)
                {
                    if (property.PropertyType == typeof(float))
                    {
                        property.SetValue(target, value, null);
                        return true;
                    }

                    if (property.PropertyType == typeof(double))
                    {
                        property.SetValue(target, (double)value, null);
                        return true;
                    }

                    if (property.PropertyType == typeof(int))
                    {
                        property.SetValue(target, (int)value, null);
                        return true;
                    }
                }
            }
            catch
            {
                // Ignore and keep trying other members.
            }

            return false;
        }
    }
}
