// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import 'vuetify/styles'
import '@mdi/font/css/materialdesignicons.css'
import { createVuetify } from 'vuetify'
import { aliases, mdi } from 'vuetify/iconsets/mdi'

export const vuetify = createVuetify({
  theme: {
    defaultTheme: 'aegisDark',
    themes: {
      aegisDark: {
        dark: true,
        colors: {
          background: '#090d16',
          surface: '#111827',
          'surface-variant': '#1e293b',
          primary: '#0ea5e9',
          secondary: '#64748b',
          accent: '#06b6d4',
          error: '#f43f5e',
          info: '#38bdf8',
          success: '#10b981',
          warning: '#f59e0b',
        },
      },
      meisterDark: {
        dark: true,
        colors: {
          background: '#090d16',
          surface: '#111827',
          primary: '#0ea5e9',
          secondary: '#64748b',
          accent: '#06b6d4',
          error: '#f43f5e',
          info: '#38bdf8',
          success: '#10b981',
          warning: '#f59e0b',
        },
      },
    },
  },
  icons: {
    defaultSet: 'mdi',
    aliases,
    sets: { mdi },
  },
  defaults: {
    global: {
      rounded: 'lg',
    },
    VBtn: {
      variant: 'flat',
      rounded: 'pill',
      color: 'primary',
    },
    VCard: { variant: 'flat', rounded: 'lg' },
    VTextField: { variant: 'outlined', density: 'comfortable' },
    VTextarea: { variant: 'outlined', density: 'comfortable' },
    VSelect: { variant: 'outlined', density: 'comfortable' },
    VAutocomplete: { variant: 'outlined', density: 'comfortable' },
    VCombobox: { variant: 'outlined', density: 'comfortable' },
    VAlert: { variant: 'tonal', density: 'comfortable' },
    VChip: { rounded: 'pill', size: 'small' },
  },
})
