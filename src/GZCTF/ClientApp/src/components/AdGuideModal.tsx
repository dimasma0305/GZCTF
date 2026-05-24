import {
  Accordion,
  Anchor,
  Code,
  Divider,
  Group,
  List,
  Modal,
  ModalProps,
  ScrollArea,
  Stack,
  Text,
  ThemeIcon,
  Title,
} from '@mantine/core'
import {
  mdiBookOpenPageVariantOutline,
  mdiClockOutline,
  mdiCounter,
  mdiCubeOutline,
  mdiKeyChain,
  mdiRestart,
  mdiShieldHalfFull,
  mdiSwordCross,
} from '@mdi/js'
import { Icon } from '@mdi/react'
import { FC } from 'react'
import { useTranslation } from 'react-i18next'
import misc from '@Styles/Misc.module.css'

interface AdGuideModalProps extends ModalProps {
  gameId: number
}

/**
 * Player-facing primer for Attack &amp; Defense. Surfaces the rules, scoring
 * math, API submission shape, and operational notes the player needs before
 * the event starts — collected in one modal instead of scattered across
 * tooltips on the per-challenge panel.
 */
export const AdGuideModal: FC<AdGuideModalProps> = ({ gameId, ...modalProps }) => {
  const { t } = useTranslation()

  const apiUrl = `${typeof window !== 'undefined' ? window.location.origin : ''}/api/Game/${gameId}/Ad`

  const curlExample = [
    `curl -X POST ${apiUrl}/Submit \\`,
    `  -H "Authorization: Bearer ad_<your-token>" \\`,
    `  -H "Content-Type: application/json" \\`,
    `  -d '{"flags":["flag{captured_from_team_b}","flag{another}"]}'`,
  ].join('\n')

  const responseExample = `{
  "acceptedCount": 1,
  "totalPoints": 10.0,
  "results": [
    { "flag": "flag{captured_from_team_b}",
      "status": "accepted", "points": 10.0,
      "flagPlantedAtRound": 7 },
    { "flag": "flag{another}",
      "status": "wrong",
      "message": "flag not recognized" }
  ]
}`

  return (
    <Modal
      size="48rem"
      centered
      title={
        <Group gap="sm">
          <ThemeIcon variant="light" color="red" size="lg">
            <Icon path={mdiBookOpenPageVariantOutline} size={1} />
          </ThemeIcon>
          <Title order={4}>
            {t('game.content.ad.guide.title', 'Attack & Defense — Player Guide')}
          </Title>
        </Group>
      }
      {...modalProps}
    >
      <ScrollArea h="70vh" scrollbarSize={6}>
        <Stack gap="md" pr="sm">
          <Text size="sm" c="dimmed">
            {t(
              'game.content.ad.guide.intro',
              'Every team gets one container per A&D challenge. Each round the platform plants a fresh flag in your container, runs a health check, and lets every team try to steal flags from every other team. Keep your service alive, patch the vulnerability, and exfiltrate the rest.'
            )}
          </Text>

          <Accordion variant="separated" defaultValue="rounds" radius="md" chevronPosition="left">
            <Accordion.Item value="rounds">
              <Accordion.Control icon={<Icon path={mdiClockOutline} size={1} color="var(--mantine-color-blue-6)" />}>
                <Text fw={600}>{t('game.content.ad.guide.rounds.title', 'Rounds & flags')}</Text>
              </Accordion.Control>
              <Accordion.Panel>
                <List size="sm" spacing={4}>
                  <List.Item>
                    {t(
                      'game.content.ad.guide.rounds.tick',
                      'A round (tick) is the scoring unit. Length is per-challenge (the operator sets it — typically 60–180s).'
                    )}
                  </List.Item>
                  <List.Item>
                    {t(
                      'game.content.ad.guide.rounds.plant',
                      'At each tick the platform writes a fresh flag into your container (the "current flag"). You can see it in the challenge modal — protect it.'
                    )}
                  </List.Item>
                  <List.Item>
                    {t(
                      'game.content.ad.guide.rounds.lifetime',
                      'Old flags stay valid for N ticks (default 5). After that they expire and cannot be submitted for points.'
                    )}
                  </List.Item>
                  <List.Item>
                    {t(
                      'game.content.ad.guide.rounds.warmup',
                      'Round 0 is warmup — no scoring yet, just make sure your service is up.'
                    )}
                  </List.Item>
                </List>
              </Accordion.Panel>
            </Accordion.Item>

            <Accordion.Item value="scoring">
              <Accordion.Control icon={<Icon path={mdiCounter} size={1} color="var(--mantine-color-teal-6)" />}>
                <Text fw={600}>{t('game.content.ad.guide.scoring.title', 'How scoring works')}</Text>
              </Accordion.Control>
              <Accordion.Panel>
                <Stack gap="sm">
                  <Text size="sm">
                    {t(
                      'game.content.ad.guide.scoring.intro',
                      'Three components add up to your team total:'
                    )}
                  </Text>
                  <List size="sm" spacing="xs">
                    <List.Item icon={<Icon path={mdiSwordCross} size={0.8} color="var(--mantine-color-teal-6)" />}>
                      <Text component="span" fw={600} c="teal">
                        {t('game.content.ad.guide.scoring.attack_label', 'Attack')}:
                      </Text>{' '}
                      <Code className={misc.ffmono}>10 / sqrt(N_capturers)</Code>{' '}
                      {t(
                        'game.content.ad.guide.scoring.attack',
                        'per flag you capture. First capturer gets 10 pts, second ~7.07, third ~5.77, and so on — being fast pays.'
                      )}
                    </List.Item>
                    <List.Item icon={<Icon path={mdiShieldHalfFull} size={0.8} color="var(--mantine-color-red-6)" />}>
                      <Text component="span" fw={600} c="red">
                        {t('game.content.ad.guide.scoring.defense_label', 'Defense loss')}:
                      </Text>{' '}
                      <Code className={misc.ffmono}>2.0 × N_times_captured ^ 0.75</Code>.{' '}
                      {t(
                        'game.content.ad.guide.scoring.defense',
                        'Each time another team captures one of your flags increments your captured-count. Sub-linear so early losses hurt more than later ones — patch fast.'
                      )}
                    </List.Item>
                    <List.Item icon={<Icon path={mdiCounter} size={0.8} color="var(--mantine-color-blue-6)" />}>
                      <Text component="span" fw={600} c="blue">
                        {t('game.content.ad.guide.scoring.sla_label', 'SLA')}:
                      </Text>{' '}
                      <Code className={misc.ffmono}>(ok_checks / total_checks) × 10 × current_round</Code>.{' '}
                      {t(
                        'game.content.ad.guide.scoring.sla',
                        'The platform checks your service every tick. Failed checks (Mumble / Offline) shrink the fraction. Keep your service responding.'
                      )}
                    </List.Item>
                  </List>
                  <Divider />
                  <Text size="sm" fw={600}>
                    {t('game.content.ad.guide.scoring.total_formula', 'Total = Attack + SLA − Defense loss')}
                  </Text>
                </Stack>
              </Accordion.Panel>
            </Accordion.Item>

            <Accordion.Item value="submit">
              <Accordion.Control icon={<Icon path={mdiKeyChain} size={1} color="var(--mantine-color-orange-6)" />}>
                <Text fw={600}>{t('game.content.ad.guide.submit.title', 'How to submit captured flags')}</Text>
              </Accordion.Control>
              <Accordion.Panel>
                <Stack gap="sm">
                  <Text size="sm">
                    {t(
                      'game.content.ad.guide.submit.api_only',
                      'Flag submission is API-only — there is no web form for A&D. Your exploit scripts batch the flags they collected and POST them in one request.'
                    )}
                  </Text>
                  <Text size="sm" fw={600}>
                    {t('game.content.ad.guide.submit.token', 'Get your token')}
                  </Text>
                  <Text size="sm" c="dimmed">
                    {t(
                      'game.content.ad.guide.submit.token_steps',
                      'Open any A&D challenge in the challenges grid. If you are the team captain, click "Generate token" / "Rotate token" — the plaintext appears exactly once. Save it somewhere your exploit scripts can read.'
                    )}
                  </Text>
                  <Text size="sm" fw={600}>
                    {t('game.content.ad.guide.submit.request', 'Submit request')}
                  </Text>
                  <Code block className={misc.ffmono} style={{ fontSize: '0.75rem' }}>
                    {curlExample}
                  </Code>
                  <Text size="sm" c="dimmed">
                    {t(
                      'game.content.ad.guide.submit.batch_note',
                      'Pass 1 to 100 flag strings per request. Results return in input order so you can correlate.'
                    )}
                  </Text>
                  <Text size="sm" fw={600}>
                    {t('game.content.ad.guide.submit.response', 'Response shape')}
                  </Text>
                  <Code block className={misc.ffmono} style={{ fontSize: '0.75rem' }}>
                    {responseExample}
                  </Code>
                  <Text size="sm" c="dimmed">
                    {t(
                      'game.content.ad.guide.submit.statuses',
                      'Per-flag status is one of: accepted | duplicate | wrong | expired | self_attack | not_started. Only accepted contributes to your score.'
                    )}
                  </Text>
                </Stack>
              </Accordion.Panel>
            </Accordion.Item>

            <Accordion.Item value="container">
              <Accordion.Control icon={<Icon path={mdiCubeOutline} size={1} color="var(--mantine-color-violet-6)" />}>
                <Text fw={600}>{t('game.content.ad.guide.container.title', 'Your container')}</Text>
              </Accordion.Control>
              <Accordion.Panel>
                <List size="sm" spacing={4}>
                  <List.Item>
                    {t(
                      'game.content.ad.guide.container.ip',
                      'The challenge modal shows your container IP:port — that\'s where checks run and where attackers point their exploits.'
                    )}
                  </List.Item>
                  <List.Item>
                    {t(
                      'game.content.ad.guide.container.patch',
                      'You can patch the binary / web service inside the container live — the platform does NOT redeploy unless you reset.'
                    )}
                  </List.Item>
                  <List.Item>
                    {t(
                      'game.content.ad.guide.container.reset',
                      'Click "Reset" to rebuild back to the baseline image. Useful if your patch broke the service, but you lose SLA during the rebuild and there is a cooldown between resets.'
                    )}
                  </List.Item>
                </List>
              </Accordion.Panel>
            </Accordion.Item>

            <Accordion.Item value="do_dont">
              <Accordion.Control icon={<Icon path={mdiRestart} size={1} color="var(--mantine-color-gray-6)" />}>
                <Text fw={600}>{t('game.content.ad.guide.do_dont.title', 'Do / Don\'t')}</Text>
              </Accordion.Control>
              <Accordion.Panel>
                <Stack gap="xs">
                  <Text size="sm" fw={600} c="teal">
                    {t('game.content.ad.guide.do_dont.do', 'Do')}
                  </Text>
                  <List size="sm" spacing={2}>
                    <List.Item>
                      {t(
                        'game.content.ad.guide.do_dont.do_patch',
                        'Patch your service before the first attack lands.'
                      )}
                    </List.Item>
                    <List.Item>
                      {t(
                        'game.content.ad.guide.do_dont.do_automate',
                        'Automate flag submission — paste into a loop that fetches the current flag from each target and POSTs the batch.'
                      )}
                    </List.Item>
                    <List.Item>
                      {t(
                        'game.content.ad.guide.do_dont.do_monitor',
                        'Watch your check status badge — if it goes Mumble/Offline you are losing SLA every tick.'
                      )}
                    </List.Item>
                  </List>
                  <Text size="sm" fw={600} c="red" mt="xs">
                    {t('game.content.ad.guide.do_dont.dont', "Don't")}
                  </Text>
                  <List size="sm" spacing={2}>
                    <List.Item>
                      {t(
                        'game.content.ad.guide.do_dont.dont_share',
                        'Share your flags or your token with other teams — operators detect it and apply penalties.'
                      )}
                    </List.Item>
                    <List.Item>
                      {t(
                        'game.content.ad.guide.do_dont.dont_dos',
                        'DoS another team\'s service or the checker infrastructure — instant DQ.'
                      )}
                    </List.Item>
                    <List.Item>
                      {t(
                        'game.content.ad.guide.do_dont.dont_break',
                        'Patch in a way that breaks the legit check (returns 500 to the checker) — you lose SLA equivalent to being offline.'
                      )}
                    </List.Item>
                  </List>
                </Stack>
              </Accordion.Panel>
            </Accordion.Item>
          </Accordion>

          <Text size="xs" c="dimmed" ta="center">
            {t(
              'game.content.ad.guide.footer',
              'Endpoint base: '
            )}
            <Anchor href={apiUrl} target="_blank" rel="noreferrer" className={misc.ffmono}>
              {apiUrl}
            </Anchor>
          </Text>
        </Stack>
      </ScrollArea>
    </Modal>
  )
}
