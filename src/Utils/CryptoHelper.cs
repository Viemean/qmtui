using System;
using System.Security.Cryptography;
using System.Text;

namespace QmTui.Utils;

/// <summary>
/// 基础加密与数据哈希辅助类
/// </summary>
public static class CryptoHelper
{
    /// <summary>
    /// 计算 UTF-8 字符串的 MD5 哈希值，并返回小写十六进制字符串
    /// </summary>
    /// <param name="input">待哈希的输入字符串</param>
    /// <returns>32 字符的小写十六进制 MD5 字符串；若输入为 null 或空则返回空字符串</returns>
    public static string ComputeMd5Hex(string? input)
    {
        if (string.IsNullOrEmpty(input)) return "";
        return Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(input)));
    }

    /// <summary>
    /// 计算 UTF-8 字符串的 MD5 哈希原始字节数组
    /// </summary>
    /// <param name="input">待哈希的输入字符串</param>
    /// <returns>16 字节的 MD5 哈希数组；若输入为 null 或空则返回空数组</returns>
    public static byte[] ComputeMd5Bytes(string? input)
    {
        if (string.IsNullOrEmpty(input)) return [];
        return MD5.HashData(Encoding.UTF8.GetBytes(input));
    }
}
