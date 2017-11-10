﻿using log4net;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.IdentityModel.Tokens;
using System.IO;
using System.Linq;
using System.Text;
using System.Web;
using System.Web.Mvc;
using System.Xml;
using TPCWare.Spid.Sdk;
using TPCWare.Spid.Sdk.IdP;
using TPCWare.Spid.WebApp.Models;

namespace TPCWare.Spid.WebApp.Controllers
{
    public class ACSController : Controller
    {
        ILog log = LogManager.GetLogger(typeof(ACSController));
        private readonly string SPID_COOKIE = ConfigurationManager.AppSettings["SPID_COOKIE"];

        public ActionResult Index()
        {
            return View();
        }

        [HttpPost]
        public ActionResult Index(FormCollection formCollection)
        {
            string idpLabel;
            string spidAuthnRequestId;

            // Try to get auth requesta data from cookie
            HttpCookie cookie = Request.Cookies[SPID_COOKIE];
            if (cookie != null)
            {
                idpLabel = cookie["IdPLabel"];
                spidAuthnRequestId = cookie["SpidAuthnRequestId"];
                log.Info($"Cookie {SPID_COOKIE} IdPLabel: {idpLabel}, SpidAuthnRequestId: {spidAuthnRequestId}");
            }
            else
            {
                log.Error("Error on ACSController [HttpPost]Index method: Impossibile recuperare l'Id della sessione.");
                ViewData["Message"] = "Impossibile recuperare i dati della sessione (cookie scaduto).";
                return View("Error");
            }

            try
            {
                IdpAuthnResponse idpAuthnResponse = SpidHelper.GetSpidAuthnResponse(formCollection["SAMLResponse"].ToString());

                if (!idpAuthnResponse.IsSuccessful)
                {
                    Session["AppUser"] = null;

                    log.Error($"Error on ACSController [HttpPost]Index method: La risposta dell'IdP riporta il seguente StatusCode: {idpAuthnResponse.StatusCodeInnerValue} con StatusMessage: {idpAuthnResponse.StatusMessage} e StatusDetail: {idpAuthnResponse.StatusDetail}.");
                    ViewData["Message"] = "La richiesta di identificazione è stata rifiutata.";
                    ViewData["ErrorMessage"] = $"StatusCode: {idpAuthnResponse.StatusCodeInnerValue} con StatusMessage: {idpAuthnResponse.StatusMessage} e StatusDetail: {idpAuthnResponse.StatusDetail}.";
                    return View("Error");
                }

                // Verifica la corrispondenza del valore di spidAuthnRequestId ricavato da cookie con quello restituito dalla risposta
                if (!SpidHelper.ValidAuthnResponse(idpAuthnResponse, spidAuthnRequestId, Request.Url.ToString()))
                {
                    Session["AppUser"] = null;

                    log.Error($"Error on ACSController [HttpPost]Index method: La risposta dell'IdP non è valida (InResponseTo != spidAuthnRequestId oppure SubjectConfirmationDataRecipient != requestPath).");
                    ViewData["Message"] = "La risposta dell'IdP non è valida perché non corrisponde alla richiesta.";
                    ViewData["ErrorMessage"] = $"RequestId: _{spidAuthnRequestId}, RequestPath: {Request.Url.ToString()}, InResponseTo: {idpAuthnResponse.InResponseTo}, Recipient: {idpAuthnResponse.SubjectConfirmationDataRecipient}.";
                    return View("Error");
                }

                // Save request and response data needed to eventually logout as a cookie
                cookie.Values["IdPLabel"] = idpLabel;
                cookie.Values["SpidAuthnRequestId"] = spidAuthnRequestId;
                cookie.Values["SubjectNameId"] = idpAuthnResponse.SubjectNameId;
                cookie.Values["AuthnStatementSessionIndex"] = idpAuthnResponse.AuthnStatementSessionIndex;
                cookie.Expires = DateTime.Now.AddMinutes(20);
                Response.Cookies.Add(cookie);

                AppUser appUser = new AppUser
                {
                    Name = SpidUserInfoHelper.Name(idpAuthnResponse.SpidUserInfo),
                    Surname = SpidUserInfoHelper.FamilyName(idpAuthnResponse.SpidUserInfo),
                     FiscalNumber = SpidUserInfoHelper.FiscalNumber(idpAuthnResponse.SpidUserInfo),
                     Email =  SpidUserInfoHelper.Email(idpAuthnResponse.SpidUserInfo)
                };

                // necessario per il checkSPID
                System.Web.HttpContext.Current.Application.Lock();

                List<AppUser> allAuthenticatedUsers = (List<AppUser>)System.Web.HttpContext.Current.Application["Users"];

                if (allAuthenticatedUsers == null)

                    allAuthenticatedUsers =  new List<AppUser>();

                allAuthenticatedUsers.Add(appUser);

                System.Web.HttpContext.Current.Application["Users"] = allAuthenticatedUsers;

                System.Web.HttpContext.Current.Application.UnLock();

                Session.Add("AppUser", appUser);

                ViewData["UserInfo"] = idpAuthnResponse.SpidUserInfo;

                return View("UserData");
            }

            catch (Exception ex)
            {
                log.Error("Error on ACSController [HttpPost]Index method", ex);
                ViewData["Message"] = "Errore nella lettura della risposta ricevuta dal provider.";
                ViewData["ErrorMessage"] = ex.Message;
                return View("Error");
            }
        }

        [HttpPost]
        public ActionResult Logout(FormCollection formCollection)
        {
            string idpLabel;
            string spidLogoutRequestId;

            // Try to get auth requesta data from cookie
            HttpCookie cookie = Request.Cookies[SPID_COOKIE];
            if (cookie != null)
            {
                idpLabel = cookie["IdPLabel"];
                spidLogoutRequestId = cookie["SpidLogoutRequestId"];
                log.Info($"Cookie {SPID_COOKIE} IdPLabel: {idpLabel}, SpidLogoutRequestId: {spidLogoutRequestId}");
            }
            else
            {
                log.Error("Error on ACSController [HttpPost]Index method: Impossibile recuperare l'Id della sessione.");
                ViewData["Message"] = "Impossibile recuperare i dati della sessione (cookie scaduto).";
                return View("Error");
            }

            // Remove the cookie
            cookie.Values["IdPLabel"] = string.Empty;
            cookie.Values["SpidAuthnRequestId"] = string.Empty;
            cookie.Values["SpidLogoutRequestId"] = string.Empty;
            cookie.Values["SubjectNameId"] = string.Empty;
            cookie.Values["AuthnStatementSessionIndex"] = string.Empty;
            cookie.Expires = DateTime.Now.AddDays(-1);
            Response.Cookies.Add(cookie);

            // End the session
            Session["AppUser"] = null;

            try
            {
                IdpLogoutResponse idpLogoutResponse = SpidHelper.GetSpidLogoutResponse(formCollection["SAMLResponse"].ToString());

                if (!idpLogoutResponse.IsSuccessful)
                {
                    log.Error($"Error on ACSController [HttpPost]Index method: La risposta dell'IdP riporta il seguente StatusCode: {idpLogoutResponse.StatusCodeInnerValue} con StatusMessage: {idpLogoutResponse.StatusMessage} e StatusDetail: {idpLogoutResponse.StatusDetail}.");
                    ViewData["Message"] = "La richiesta di logout è stata rifiutata.";
                    ViewData["ErrorMessage"] = $"StatusCode: {idpLogoutResponse.StatusCodeInnerValue} con StatusMessage: {idpLogoutResponse.StatusMessage} e StatusDetail: {idpLogoutResponse.StatusDetail}.";
                    return View("Error");
                }

                // Verifica la corrispondenza del valore di spidLogoutRequestId ricavato da cookie con quello restituito dalla risposta
                if (!SpidHelper.ValidLogoutResponse(idpLogoutResponse, spidLogoutRequestId))
                {
                    log.Error($"Error on ACSController [HttpPost]Index method: La risposta dell'IdP non è valida (InResponseTo != spidLogoutRequestId).");
                    ViewData["Message"] = "La risposta dell'IdP non è valida perché non corrisponde alla richiesta.";
                    ViewData["ErrorMessage"] = $"RequestId: _{spidLogoutRequestId}, RequestPath: {Request.Url.ToString()}, InResponseTo: {idpLogoutResponse.InResponseTo}.";
                    return View("Error");
                }

                return View("Logout");
            }

            catch (Exception ex)
            {
                log.Error("Error on ACSController [HttpPost]Index method", ex);
                ViewData["Message"] = "Errore nella lettura della risposta ricevuta dal provider.";
                ViewData["ErrorMessage"] = ex.Message;
                return View("Error");
            }
        }

    }
}