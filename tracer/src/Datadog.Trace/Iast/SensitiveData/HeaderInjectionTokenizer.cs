// <copyright file="HeaderInjectionTokenizer.cs" company="Datadog">
// Unless explicitly stated otherwise all files in this repository are licensed under the Apache 2 License.
// This product includes software developed at Datadog (https://www.datadoghq.com/). Copyright 2017 Datadog, Inc.
// </copyright>

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Datadog.Trace.Configuration;
using Datadog.Trace.Logging;

#nullable enable

namespace Datadog.Trace.Iast.SensitiveData;

/// <summary>
/// We are already redacting sensitive values in the evidence (header values),
/// but if the key of the returned header matches a regex, we should also redact the evidence value after ":"
/// </summary>
internal class HeaderInjectionTokenizer : ITokenizer
{
    // We should add this timeout to all the sensitive data tokenizers and have a common timeout
    private static readonly IDatadogLogger _logger = DatadogLogging.GetLoggerFor(typeof(HeaderInjectionTokenizer));
    private static TimeSpan _timeout = TimeSpan.FromMilliseconds(100);
    private static Regex _keyPattern = new Regex(@"(?i)(?:p(?:ass)?w(?:or)?d|pass(?:_?phrase)?|secret|(?:api_?|private_?|public_?|access_?|secret_?)key(?:_?id)?|token|consumer_?(?:id|key|secret)|sign(?:ed|ature)?|auth(?:entication|orization)?)", RegexOptions.Compiled | RegexOptions.IgnoreCase, _timeout);
    private static Regex _valuePattern = new Regex(@"(?i)(?:bearer\s+[a-z0-9\._\-]+|glpat-[\w\-]{20}|gh[opsu]_[0-9a-zA-Z]{36}|ey[I-L][\w=\-]+\.ey[I-L][\w=\-]+(?:\.[\w.+/=\-]+)?|(?:[\-]{5}BEGIN[a-z\s]+PRIVATE\sKEY[\-]{5}[^\-]+[\-]{5}END[a-z\s]+PRIVATE\sKEY[\-]{5}|ssh-rsa\s*[a-z0-9/\.+]{100,}))", RegexOptions.Compiled | RegexOptions.IgnoreCase, _timeout);

    public HeaderInjectionTokenizer()
    {
    }

    public List<Range> GetTokens(string evidence, IntegrationId? integrationId = null)
    {
        var separatorStart = evidence.IndexOf(IastModule.HeaderInjectionEvidenceSeparator);

        if (separatorStart > 0)
        {
            var separatorEnd = separatorStart + IastModule.HeaderInjectionEvidenceSeparator.Length;

            // If the key patterns applies to the key or the value pattern applies to the value,
            // we should redact the value

            try
            {
                if (_keyPattern.IsMatch(evidence.Substring(0, separatorStart)) ||
                    _valuePattern.IsMatch(evidence, separatorEnd))
                {
                    return [new Range(separatorEnd, evidence.Length - separatorEnd)];
                }
            }
            catch (RegexMatchTimeoutException)
            {
                _logger.Warning("Regex match timeout in HeaderInjectionTokenizer.");
            }
        }

        return [];
    }
}