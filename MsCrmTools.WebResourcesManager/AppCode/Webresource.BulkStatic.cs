﻿using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace MscrmTools.WebresourcesManager.AppCode
{
    public partial class Webresource
    {
        public static readonly ColumnSet Columns = new ColumnSet(
            "languagecode", "createdon", "name", "dependencyxml", "modifiedby",
            "webresourcetype", "displayname", "modifiedon", "createdby",
            "webresourceid", "description", "content");

        public static readonly Regex InValidWrNameRegex = new Regex("[^a-z0-9A-Z_\\./]|[/]{2,}", (RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase));

        public static readonly Regex InValidWrNameRegexForV9 = new Regex("[^a-z0-9A-Z_\\-\\./]|[/]{2,}", (RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase));

        public static readonly ColumnSet LazyLoadingColumns = new ColumnSet(
                            "languagecode", "createdon", "name", "dependencyxml", "modifiedby",
            "webresourcetype", "displayname", "modifiedon", "createdby",
            "webresourceid", "description");

        private static readonly HashSet<string> ExtensionsToSkipLoadingErrorMessage = new HashSet<string> { "map", "ts" };
        private static readonly HashSet<string> ValidExtensions = new HashSet<string> { "htm", "html", "css", "js", "json", "xml", "jpg", "jpeg", "png", "gif", "ico", "xap", "xslt", "svg", "resx" };

        public static void AddToSolution(List<Webresource> resources, string solutionUniqueName, IOrganizationService service)
        {
            var bulkRequest = new ExecuteMultipleRequest
            {
                Settings = new ExecuteMultipleSettings
                {
                    ContinueOnError = true,
                    ReturnResponses = false
                },
                Requests = new OrganizationRequestCollection()
            };

            foreach (var resource in resources)
            {
                bulkRequest.Requests.Add(new AddSolutionComponentRequest
                {
                    AddRequiredComponents = false,
                    ComponentId = resource.Id,
                    ComponentType = 61, // Webresource
                    SolutionUniqueName = solutionUniqueName
                });
            }

            if (bulkRequest.Requests.Count == 1)
            {
                service.Execute(bulkRequest.Requests.First());
            }
            else
            {
                service.Execute(bulkRequest);
            }
        }

        public static bool IsNameValid(string name, int organizationMajorVersion)
        {
            if (InValidWrNameRegex.IsMatch(name) && organizationMajorVersion < 9 && organizationMajorVersion > 0
            || InValidWrNameRegexForV9.IsMatch(name) && (organizationMajorVersion <= 0 || organizationMajorVersion >= 9))
            {
                return false;
            }

            const string pattern = "*.config .* _* *.bin";

            // insert backslash before regex special characters that may appear in filenames
            var regexPattern = Regex.Replace(pattern, @"([\+\(\)\[\]\{\$\^\.])", @"\$1");

            // apply regex syntax
            regexPattern = $"^{regexPattern.Replace(" ", "$|^").Replace("*", ".*")}$";

            var regex = new Regex(regexPattern, RegexOptions.IgnoreCase);

            return !regex.IsMatch(name);
        }

        public static void Publish(List<Webresource> webresources, IOrganizationService service)
        {
            string idsXml = string.Empty;

            foreach (Webresource webresource in webresources)
            {
                idsXml += $"<webresource>{webresource.Id:B}</webresource>";
            }

            var pxReq1 = new PublishXmlRequest
            {
                ParameterXml = $"<importexportxml><webresources>{idsXml}</webresources></importexportxml>"
            };

            service.Execute(pxReq1);

            foreach (Webresource webresource in webresources)
            {
                webresource.GetLatestVersion(true);
            }
        }

        public static IEnumerable<Webresource> RetrieveFromDirectory(MyPluginControl parent, Settings settings, string path, List<string> extensionsToLoad, List<string> invalidFilenames, int organizationMajorVersion)
        {
            if (!Directory.Exists(path))
                throw new DirectoryNotFoundException(path);

            var list = new List<Webresource>();

            var di = new DirectoryInfo(path);

            // Only folder ending with "_" character are processed. They represent
            // the prefix of customizations
            foreach (DirectoryInfo diChild in di.GetDirectories("*_", SearchOption.TopDirectoryOnly))
            {
                LoadFilesFromFolder(parent, settings, extensionsToLoad, invalidFilenames, diChild, di.FullName, list, organizationMajorVersion);
            }

            LoadFilesFromFolder(parent, settings, extensionsToLoad, invalidFilenames, di, di.FullName, list, organizationMajorVersion);

            return list;
        }

        public static Entity RetrieveWebresource(string name, IOrganizationService service)
        {
            try
            {
                var qba = new QueryByAttribute("webresource");
                qba.Attributes.Add("name");
                qba.Values.Add(name);
                qba.ColumnSet = Webresource.Columns;

                EntityCollection collection = service.RetrieveMultiple(qba);

                if (collection.Entities.Count > 1)
                {
                    throw new Exception($"there are more than one web resource with name '{name}'");
                }

                return collection.Entities.FirstOrDefault();
            }
            catch (Exception error)
            {
                throw new Exception($"An error occured while retrieving a webresource with name {name}: {error.Message}");
            }
        }

        public static Entity RetrieveWebresource(Guid id, IOrganizationService service)
        {
            try
            {
                if (id == null || id == Guid.Empty)
                    throw new Exception($"WebResource id is null or empty");

                return service.Retrieve("webresource", id, Webresource.Columns);
            }
            catch (Exception error)
            {
                throw new Exception($"An error occured while retrieving a webresource with id {id}: {error.Message}");
            }
        }

        public static IEnumerable<Webresource> RetrieveWebresources(MyPluginControl parent, IOrganizationService service, Settings settings, Guid solutionId, List<int> types, bool filterByLcid = false, params int[] lcids)
        {
            try
            {
                if (solutionId == Guid.Empty)
                {
                    var qe = new QueryExpression("webresource")
                    {
                        ColumnSet = settings.LazyLoadingOfWebResources ? Webresource.LazyLoadingColumns : Webresource.Columns,
                        Criteria = new FilterExpression
                        {
                            Filters =
                            {
                                settings.LoadSystemHiddenResources == false ? new FilterExpression
                                {
                                    FilterOperator = LogicalOperator.And,
                                    Conditions =
                                    {
                                        new ConditionExpression("ishidden", ConditionOperator.Equal, false)
                                    }
                                }:new FilterExpression(),
                                new FilterExpression
                                {
                                    FilterOperator = LogicalOperator.Or,
                                     Conditions =
                                    {
                                        new ConditionExpression("ismanaged", ConditionOperator.Equal, false),
                                        new ConditionExpression("ismanaged", ConditionOperator.Equal, settings.LoadManaged),
                                        new ConditionExpression("iscustomizable", ConditionOperator.Equal, true),
                                    }
                                }
                            }
                        },
                        Orders = { new OrderExpression("name", OrderType.Ascending) },
                        PageInfo = new PagingInfo
                        {
                            Count = 250,
                            PageNumber = 1
                        }
                    };

                    if (!string.IsNullOrEmpty(settings.ExcludedPrefixes))
                    {
                        var prefixes = settings.ExcludedPrefixes.Split(',');

                        foreach (var prefix in prefixes)
                        {
                            qe.Criteria.Filters.First().AddCondition("name", ConditionOperator.DoesNotBeginWith, prefix);
                        }
                    }

                    if (!string.IsNullOrEmpty(settings.IncludedPrefixes))
                    {
                        var prefixes = settings.IncludedPrefixes.Split(',');
                        var prefixFilter = new FilterExpression(LogicalOperator.Or);
                        qe.Criteria.Filters.First().AddFilter(prefixFilter);

                        foreach (var prefix in prefixes)
                        {
                            prefixFilter.AddCondition("name", ConditionOperator.BeginsWith, prefix);
                        }
                    }

                    if (filterByLcid && lcids.Length != 0)
                    {
                        var lcidFilter = qe.Criteria.Filters.First().AddFilter(LogicalOperator.Or);
                        lcidFilter.AddCondition("languagecode", ConditionOperator.In, lcids.Select(l => (object)l).ToArray());
                        lcidFilter.AddCondition("languagecode", ConditionOperator.Null);
                    }

                    if (types.Count != 0)
                    {
                        qe.Criteria.Filters.First().Conditions.Add(new ConditionExpression("webresourcetype", ConditionOperator.In, types.ToArray()));
                    }

                    EntityCollection ec;
                    List<Webresource> resources = new List<Webresource>();
                    do
                    {
                        ec = service.RetrieveMultiple(qe);

                        resources.AddRange(ec.Entities.Select(e => new Webresource(e, parent, settings)));

                        qe.PageInfo.PageNumber++;
                        qe.PageInfo.PagingCookie = ec.PagingCookie;
                    } while (ec.MoreRecords);

                    return resources;
                }

                var qba = new QueryByAttribute("solutioncomponent") { ColumnSet = new ColumnSet("objectid") };
                qba.Attributes.AddRange("solutionid", "componenttype");
                qba.Values.AddRange(solutionId, 61);

                var components = service.RetrieveMultiple(qba).Entities;

                var list =
                    components.Select(component => component.GetAttributeValue<Guid>("objectid").ToString("B"))
                        .ToList();

                if (list.Count > 0)
                {
                    var qe = new QueryExpression("webresource")
                    {
                        ColumnSet = settings.LazyLoadingOfWebResources ? Webresource.LazyLoadingColumns : Webresource.Columns,
                        Criteria = new FilterExpression
                        {
                            Filters =
                            {
                                new FilterExpression
                                {
                                    FilterOperator = LogicalOperator.And,
                                    Conditions =
                                    {
                                        new ConditionExpression("ishidden", ConditionOperator.Equal, false),
                                        new ConditionExpression("webresourceid", ConditionOperator.In, list.ToArray()),
                                    }
                                },
                                new FilterExpression
                                {
                                    FilterOperator = LogicalOperator.Or,
                                     Conditions =
                                    {
                                        new ConditionExpression("ismanaged", ConditionOperator.Equal, false),
                                        new ConditionExpression("ismanaged", ConditionOperator.Equal, settings.LoadManaged),
                                        new ConditionExpression("iscustomizable", ConditionOperator.Equal, true),
                                    }
                                }
                            }
                        },
                        Orders = { new OrderExpression("name", OrderType.Ascending) }
                    };

                    if (types.Count != 0)
                    {
                        qe.Criteria.Filters.First().Conditions.Add(new ConditionExpression("webresourcetype", ConditionOperator.In, types.ToArray()));
                    }

                    if (filterByLcid && lcids.Length != 0)
                    {
                        var lcidFilter = qe.Criteria.Filters.First().AddFilter(LogicalOperator.Or);
                        lcidFilter.AddCondition("languagecode", ConditionOperator.In, lcids.Select(l => (object)l).ToArray());
                        lcidFilter.AddCondition("languagecode", ConditionOperator.Null);
                    }

                    return service.RetrieveMultiple(qe).Entities.Select(e => new Webresource(e, parent, settings));
                }

                return new List<Webresource>();
            }
            catch (Exception error)
            {
                throw new Exception($"An exception occured while retrieving webresources: {error.Message}");
            }
        }

        private static string GetRelativePath(string rootPath, string path)
        {
            path = path.Replace(rootPath, string.Empty)
                       .Remove(0, 1)
                       .Replace("\\", "/");
            return path;
        }

        private static void LoadFilesFromFolder(MyPluginControl parent, Settings settings, List<string> extensionsToLoad, List<string> invalidFilenames,
                                            DirectoryInfo di, string rootPath, List<Webresource> list, int organizationMajorVersion)
        {
            if (di.FullName != rootPath)
            {
                foreach (var diChild in di.GetDirectories())
                {
                    LoadFilesFromFolder(parent, settings, extensionsToLoad, invalidFilenames, diChild, rootPath, list, organizationMajorVersion);
                }
            }

            var fileInfos = di.GetFiles("*", SearchOption.TopDirectoryOnly);
            var files = new HashSet<string>(fileInfos.Select(f => f.FullName));
            var extensionlessFiles = new HashSet<string>();
            var ignoredLocalFiles = new List<string>();
            ignoredLocalFiles.AddRange(settings.IgnoredLocalFiles);
            ignoredLocalFiles.AddRange(settings.IgnoredLocalFiles.Select(f => f.Replace("\\", "/")));
            foreach (var fi in fileInfos.Where(f => extensionsToLoad.Contains(f.Extension)))
            {
                var relativePath = GetRelativePath(rootPath, fi.FullName);

                if (ignoredLocalFiles.Contains(relativePath, StringComparer.CurrentCultureIgnoreCase)
                    || ignoredLocalFiles.Contains(fi.FullName, StringComparer.CurrentCultureIgnoreCase))
                {
                    // File is set to be ignored, ignore it.
                    continue;
                }

                if (fi.Extension.Length == 0
                    || !IsNameValid(fi.Name, organizationMajorVersion)
                    || !extensionsToLoad.Contains(fi.Extension))
                {
                    invalidFilenames.Add(fi.FullName);
                    continue;
                }

                var resource = new Webresource(relativePath, fi.FullName,
                    GetTypeFromExtension(fi.Extension.Remove(0, 1)), parent, settings);
                var extensionless = Path.Combine(fi.DirectoryName ?? "", Path.GetFileNameWithoutExtension(fi.FullName));
                if (files.Contains(extensionless))
                {
                    resource.ExtensionlessMappingFilePath = extensionless;
                    extensionlessFiles.Add(extensionless);
                }

                if (parent.WebresourcesCache.All(r => !string.Equals(r.Name, resource.Name, StringComparison.CurrentCultureIgnoreCase)))
                {
                    parent.WebresourcesCache.Add(resource);
                }

                list.Add(resource);
            }

            invalidFilenames.RemoveAll(f => extensionlessFiles.Contains(f));
        }
    }
}