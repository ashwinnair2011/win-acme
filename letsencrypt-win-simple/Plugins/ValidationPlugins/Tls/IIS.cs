﻿using ACMESharp;
using Autofac;
using PKISharp.WACS.Clients;
using PKISharp.WACS.Plugins.Base;
using PKISharp.WACS.Plugins.Interfaces;
using PKISharp.WACS.Services;
using Microsoft.Web.Administration;
using System;
using System.Linq;
using static PKISharp.WACS.Clients.IISClient;

namespace PKISharp.WACS.Plugins.ValidationPlugins.Tls
{
    /// <summary>
    /// Use IIS to make the certificate available
    /// </summary>
    internal class IISFactory : BaseValidationPluginFactory<IIS>
    {
        private IISClient _iisClient;

        public IISFactory(ILogService log, IISClient iisClient) :
            base(log, "IIS", "Use IIS as endpoint", AcmeProtocol.CHALLENGE_TYPE_SNI)
        {
            _iisClient = iisClient;
        }

        public override bool Hidden => true;
        public override bool CanValidate(Target target) => _iisClient.Version.Major >= 8;
    }

    internal class IIS : BaseTlsValidation
    {
        private long? _tempSiteId;
        private bool _tempSiteCreated = false;

        private IISClient _iisClient;
        private IStorePlugin _storePlugin;
   
        public IIS(ILogService log, IStorePlugin store, ScheduledRenewal target, IISClient iisClient, string identifier) : 
            base(log, target, identifier)
        {
            _storePlugin = store;
            _iisClient = iisClient;
            _tempSiteId = target.Binding.ValidationSiteId ?? target.Binding.TargetSiteId;
        }

        public override void InstallCertificate(ScheduledRenewal renewal, CertificateInfo certificate)
        {
            _storePlugin.Save(certificate);
            AddToIIS(certificate);
        }

        public override void RemoveCertificate(ScheduledRenewal renewal, CertificateInfo certificate)
        {
            _storePlugin.Delete(certificate);
            RemoveFromIIS(certificate);
        }

        private void AddToIIS(CertificateInfo certificate)
        {
            var host = certificate.HostNames.First();
            Site site;
            if (_tempSiteId == null || _tempSiteId == 0)
            {
                site = _iisClient.ServerManager.Sites.Add(host, "http", $"*:80:{host}", "X:\\");
                _tempSiteId = site.Id;
                _tempSiteCreated = true;
            }
            else
            {
                site = _iisClient.WebSites.Where(x => x.Id == _tempSiteId).FirstOrDefault();
                if (site == null)
                {
                    _log.Error("Unable to find IIS SiteId {Id} which is required for validation", _tempSiteId);
                    return;
                }
            }

            var flags = SSLFlags.SNI;
            if (certificate.Store == null)
            {
                flags |= SSLFlags.CentralSSL;
            }
            _iisClient.AddOrUpdateBindings(site, host, flags, certificate.Certificate.GetCertHash(), certificate.Store?.Name, 443, "*", false);
            _iisClient.Commit();
        }

        private void RemoveFromIIS(CertificateInfo certificate)
        {
            var host = certificate.HostNames.First();
            if (_tempSiteId != null)
            {
                var site = _iisClient.ServerManager.Sites.Where(x => x.Id == _tempSiteId).FirstOrDefault();
                if (_tempSiteCreated)
                {
                    _iisClient.ServerManager.Sites.Remove(site);
                }
                else
                {
                    var binding = site.Bindings.Where(x => string.Equals(x.Host, host, StringComparison.InvariantCultureIgnoreCase)).FirstOrDefault();
                    site.Bindings.Remove(binding);
                }
                _iisClient.Commit();
            }
        }
    }
}
