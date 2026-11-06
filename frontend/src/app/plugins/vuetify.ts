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
          background: '#000000',
          surface: '#111111',
          'surface-variant': '#1e1e1e',
          primary: '#ec4899',
          secondary: '#b4b4b4',
          accent: '#ec4899',
          error: '#e5566f',
          info: '#a1a1aa',
          success: '#3fc07a',
          warning: '#f2b33d',
        },
      },
      meisterDark: {
        dark: true,
        colors: {
          background: '#000000',
          surface: '#111111',
          primary: '#ec4899',
          secondary: '#b4b4b4',
          accent: '#ec4899',
          error: '#e5566f',
          info: '#a1a1aa',
          success: '#3fc07a',
          warning: '#f2b33d',
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
      rounded: 'lg',
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
