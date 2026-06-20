using System;
using System.Collections.Generic;
using System.Reflection;

namespace AutoRocketFuelPlanner
{
    /*
     * ========================= 阅读顺序导图 =========================
     * 这是“反射工具底座”，建议顺序：
     * 1) GetMembers：先理解“候选成员集合”怎么来。
     * 2) TryReadFloat：看如何把不同数值类型统一读成 float。
     * 3) TryWriteFloat：看如何把 float 安全写回不同类型成员。
     *
     * 关键理解：
     * - 这里任何失败都返回 false，不抛异常；
     * - 上层会继续尝试下一个候选成员，因此具备容错性。
     * ==============================================================
     */
    /// <summary>
    /// 反射工具：
    /// - 用“尽力而为（best effort）”方式读写字段/属性；
    /// - 避免直接耦合 ONI 内部类型成员名，提升跨版本兼容性；
    /// - 所有异常都吞掉并返回 false，由上层继续尝试其他候选成员。
    /// </summary>
    internal static class ReflectionHelpers
    {
        /// <summary>
        /// 枚举实例上的所有字段和非索引属性。
        /// 说明：
        /// - 包含 public/private；
        /// - 不包含静态成员；
        /// - 属性排除索引器（避免参数调用复杂化）。
        /// </summary>
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

        /// <summary>
        /// 尝试把字段/属性读取为 float：
        /// 支持 float/int/double 三种常见数值类型。
        /// </summary>
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

        /// <summary>
        /// 尝试把 float 写回字段/属性：
        /// 支持目标类型 float/double/int。
        /// </summary>
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
