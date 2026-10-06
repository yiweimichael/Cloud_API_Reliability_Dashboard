import { InteractionRequiredAuthError, PublicClientApplication } from '@azure/msal-browser'

export const msal = new PublicClientApplication({
  auth: {
    clientId: '1289d6a3-20ad-4cf8-9e5e-419be4fa06cb', // SPA app registration (apidash-web)
    // External ID uses ciamlogin.com. The tenant ID path matters: the tenant's metadata issuer is
    // GUID-based, and MSAL rejects it if the authority only names the tenant by its subdomain.
    authority: 'https://apidashusers.ciamlogin.com/52294bb8-24c0-4c40-bdcf-4c3a5a05721b/',
    redirectUri: window.location.origin,
  },
  // Keep the session across tabs and reloads.
  cache: { cacheLocation: 'localStorage' },
})

export const apiScopes = ['api://6829060d-15ff-4626-abd8-f4df997c3bb9/access_as_user']

export async function getAccessToken() {
  const account = msal.getActiveAccount() ?? undefined
  try {
    return (await msal.acquireTokenSilent({ scopes: apiScopes, account })).accessToken
  } catch (e) {
    // Refresh token expired or consent changed: send the user back through sign-in.
    if (e instanceof InteractionRequiredAuthError) {
      await msal.acquireTokenRedirect({ scopes: apiScopes, account })
    }
    throw e
  }
}
