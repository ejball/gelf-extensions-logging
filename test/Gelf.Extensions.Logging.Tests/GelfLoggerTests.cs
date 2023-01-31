﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Bogus;
using Gelf.Extensions.Logging.Tests.Fixtures;
using Microsoft.CSharp.RuntimeBinder;
using Microsoft.Extensions.Logging;
using Xunit;

// ReSharper disable TemplateIsNotCompileTimeConstantProblem
// ReSharper disable InconsistentLogPropertyNaming

namespace Gelf.Extensions.Logging.Tests
{
    public abstract class GelfLoggerTests : IDisposable
    {
        protected readonly Faker Faker;
        protected readonly GraylogFixture GraylogFixture;
        protected readonly LoggerFixture LoggerFixture;

        protected GelfLoggerTests(GraylogFixture graylogFixture, LoggerFixture loggerFixture)
        {
            GraylogFixture = graylogFixture;
            LoggerFixture = loggerFixture;
            Faker = new Faker();
        }

        public void Dispose()
        {
            LoggerFixture.Dispose();
        }

        [Theory]
        [InlineData(LogLevel.Critical, 2)]
        [InlineData(LogLevel.Error, 3)]
        [InlineData(LogLevel.Warning, 4)]
        [InlineData(LogLevel.Information, 6)]
        [InlineData(LogLevel.Debug, 7)]
        [InlineData(LogLevel.Trace, 7)]
        public async Task Sends_message_to_Graylog(LogLevel logLevel, int expectedLevel)
        {
            var messageText = Faker.Lorem.Sentence();
            var sut = LoggerFixture.CreateLogger<GelfLoggerTests>();

            sut.Log(logLevel, new EventId(), (object) null, null, (_, _) => messageText);

            var message = await GraylogFixture.WaitForMessageAsync();

            Assert.NotEmpty(message._id);
            Assert.Equal(LoggerFixture.LoggerOptions.LogSource, message.source);
            Assert.Equal(messageText, message.message);
            Assert.Equal(expectedLevel, message.level);
            Assert.Equal(typeof(GelfLoggerTests).FullName, message.logger);
            Assert.Throws<RuntimeBinderException>(() => message.exception);
            Assert.Throws<RuntimeBinderException>(() => message.message_template);
            Assert.Throws<RuntimeBinderException>(() => message.event_id);
        }

        [Fact]
        public async Task Includes_exceptions_on_messages()
        {
            var messageText = Faker.Lorem.Sentence();
            var exception = new Exception("Something went wrong!");
            var sut = LoggerFixture.CreateLogger<GelfLoggerTests>();

            sut.LogError(new EventId(), exception, messageText);

            var message = await GraylogFixture.WaitForMessageAsync();

            Assert.Equal(messageText, message.message);
            Assert.Equal(exception.ToString(), message.exception);
        }

        [Fact]
        public async Task Includes_event_IDs_on_messages()
        {
            var messageText = Faker.Lorem.Sentence();

            var sut = LoggerFixture.CreateLogger<GelfLoggerTests>();
            sut.LogInformation(new EventId(197, "foo"), messageText);

            var message = await GraylogFixture.WaitForMessageAsync();

            Assert.Equal(messageText, message.message);
            Assert.Equal(197, message.event_id);
            Assert.Equal("foo", message.event_name);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task Omits_optional_fields_via_option(bool omits)
        {
            var options = LoggerFixture.LoggerOptions;
            options.OmitOptionalFields = omits;
            var messageText = Faker.Lorem.Sentence();
            var exception = new Exception("Something went wrong!");

            using var loggerFactory = LoggerFixture.CreateLoggerFactory(options);
            var sut = loggerFactory.CreateLogger(nameof(GelfLoggerTests));
            sut.LogError(new EventId(197, "foo"), exception, messageText);

            var message = await GraylogFixture.WaitForMessageAsync();

            Assert.Equal(messageText, message.message);

            if (omits)
            {
                Assert.Throws<RuntimeBinderException>(() => message.logger);
                Assert.Throws<RuntimeBinderException>(() => message.exception);
                Assert.Throws<RuntimeBinderException>(() => message.event_id);
                Assert.Throws<RuntimeBinderException>(() => message.event_name);
            }
            else
            {
                Assert.Equal(nameof(GelfLoggerTests), message.logger);
                Assert.Equal(exception.ToString(), message.exception);
                Assert.Equal(197, message.event_id);
                Assert.Equal("foo", message.event_name);
            }
        }

        [Fact]
        public async Task Sends_message_with_additional_fields_from_options()
        {
            var options = LoggerFixture.LoggerOptions;
            options.AdditionalFields["foo"] = "foo";
            options.AdditionalFields["bar"] = "bar";
            options.AdditionalFields["quux"] = 123;
            var messageText = Faker.Lorem.Sentence();

            using var loggerFactory = LoggerFixture.CreateLoggerFactory(options);
            var sut = loggerFactory.CreateLogger(nameof(GelfLoggerTests));
            sut.LogInformation(messageText);

            var message = await GraylogFixture.WaitForMessageAsync();

            Assert.Equal("foo", message.foo);
            Assert.Equal("bar", message.bar);
            Assert.Equal(123, message.quux);
        }

        [Fact]
        public async Task Sends_message_with_additional_fields_from_scope()
        {
            var messageText = Faker.Lorem.Sentence();
            var sut = LoggerFixture.CreateLogger<GelfLoggerTests>();
            using (sut.BeginScope(new Dictionary<string, object>
            {
                ["baz"] = "baz",
                ["qux"] = "qux",
                ["quux"] = 123
            }))
            {
                using (sut.BeginScope(("quuz", 456.5)))
                {
                    sut.LogCritical(messageText);
                }
            }

            var message = await GraylogFixture.WaitForMessageAsync();

            Assert.Equal("baz", message.baz);
            Assert.Equal("qux", message.qux);
            Assert.Equal(123, message.quux);
            Assert.Equal(456.5, message.quuz);
        }

        [Fact]
        public async Task Sends_message_without_additional_fields_from_scope_when_scope_is_not_included()
        {
            var options = LoggerFixture.LoggerOptions;
            options.IncludeScopes = false;
            options.AdditionalFields.Add("test_id", TestContext.TestId);

            using var loggerFactory = LoggerFixture.CreateLoggerFactory(options);
            var sut = loggerFactory.CreateLogger(nameof(GelfLoggerTests));
            using (sut.BeginScope(("foo", "bar")))
            {
                sut.LogInformation(Faker.Lorem.Sentence());
            }

            var message = await GraylogFixture.WaitForMessageAsync();

            Assert.Throws<RuntimeBinderException>(() => message.foo);
        }

        [Fact]
        public async Task Uses_inner_scope_fields_when_keys_duplicated_in_multiple_scopes()
        {
            var sut = LoggerFixture.CreateLogger<GelfLoggerTests>();
            using (sut.BeginScope(("foo", "outer")))
            {
                using (sut.BeginScope(("foo", "inner")))
                {
                    sut.LogCritical(Faker.Lorem.Sentence());
                }
            }

            var message = await GraylogFixture.WaitForMessageAsync();

            Assert.Equal("inner", message.foo);
        }

        [Fact]
        public async Task Sends_message_with_additional_fields_from_structured_log()
        {
            var sut = LoggerFixture.CreateLogger<GelfLoggerTests>();
            sut.LogDebug("Structured log line with {first_value} and {second_value}", "foo", 123);

            var message = await GraylogFixture.WaitForMessageAsync();

            Assert.Equal("Structured log line with foo and 123", message.message);
            Assert.Equal("foo", message.first_value);
            Assert.Equal(123, message.second_value);
        }

        [Fact]
        public async Task Uses_structured_log_fields_when_keys_duplicated_in_scope()
        {
            var sut = LoggerFixture.CreateLogger<GelfLoggerTests>();
            using (sut.BeginScope(("foo", "scope")))
            {
                sut.LogDebug("Structured log line with {foo}", "structured");
            }

            var message = await GraylogFixture.WaitForMessageAsync();

            Assert.Equal("structured", message.foo);
        }

        [Fact]
        public async Task Ignores_null_values_in_additional_fields()
        {
            var options = LoggerFixture.LoggerOptions;
            options.AdditionalFields["foo"] = null;

            using var loggerFactory = LoggerFixture.CreateLoggerFactory(options);
            var sut = loggerFactory.CreateLogger(nameof(GelfLoggerTests));

            using (sut.BeginScope(("bar", (string) null)))
            using (sut.BeginScope(new Dictionary<string, object>
            {
                ["baz"] = null
            }))
            {
                sut.LogInformation(Faker.Lorem.Sentence());
            }

            var message = await GraylogFixture.WaitForMessageAsync();

            Assert.Throws<RuntimeBinderException>(() => message.foo);
            Assert.Throws<RuntimeBinderException>(() => message.bar);
            Assert.Throws<RuntimeBinderException>(() => message.baz);
        }

        [Fact]
        public async Task Sends_message_templates_when_enabled()
        {
            var options = LoggerFixture.LoggerOptions;
            options.IncludeMessageTemplates = true;
            options.AdditionalFields.Add("test_id", TestContext.TestId);

            using var loggerFactory = LoggerFixture.CreateLoggerFactory(options);
            var messageTemplate = "This is a message template {foo} {bar}";
            var sut = loggerFactory.CreateLogger(nameof(GelfLoggerTests));
            sut.LogInformation(messageTemplate, "FOO", "BAR");

            var message = await GraylogFixture.WaitForMessageAsync();

            Assert.Equal(messageTemplate, message.message_template);
            Assert.Equal("FOO", message.foo);
            Assert.Equal("BAR", message.bar);
        }

        [Fact]
        public async Task Sends_message_with_additional_fields_from_factory()
        {
            var options = LoggerFixture.LoggerOptions;
            options.AdditionalFieldsFactory = (originalLogLevel, originalEvent, originalException) =>
                new Dictionary<string, object>
                {
                    ["log_level"] = originalLogLevel.ToString(),
                    ["exception_type"] = originalException?.GetType().ToString(),
                    ["custom_event_name"] = originalEvent.Name
                };

            using var loggerFactory = LoggerFixture.CreateLoggerFactory(options);
            var messageText = Faker.Lorem.Sentence();
            var exception = new Exception("Something went wrong!");
            var eventId = new EventId(250, "foo");
            var sut = loggerFactory.CreateLogger(nameof(GelfLoggerTests));

            sut.LogError(eventId, exception, messageText);

            var message = await GraylogFixture.WaitForMessageAsync();

            Assert.Equal(messageText, message.message);
            Assert.Equal(exception.ToString(), message.exception);
            Assert.Equal("Error", message.log_level);
            Assert.Equal(exception.GetType().ToString(), message.exception_type);
            Assert.Equal("foo", message.custom_event_name);
        }

        [Fact]
        public async Task Sends_activity_tracking_additional_fields()
        {
            using var activity = new Activity("an activity").Start();
            Activity.Current = activity;

            using var loggerFactory = LoggerFixture.CreateLoggerFactory(
                LoggerFixture.LoggerOptions, ActivityTrackingOptions.TraceId|ActivityTrackingOptions.SpanId);
            var sut = loggerFactory.CreateLogger(nameof(GelfLoggerTests));

            sut.LogInformation(Faker.Lorem.Sentence());
            var message = await GraylogFixture.WaitForMessageAsync();

            Assert.Equal(Activity.Current.TraceId.ToString(), message.TraceId);
            Assert.Equal(Activity.Current.SpanId.ToString(), message.SpanId);
        }
    }
}
