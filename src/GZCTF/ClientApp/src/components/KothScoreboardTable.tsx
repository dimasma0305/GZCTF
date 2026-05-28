import {
  Badge,
  Box,
  Center,
  Group,
  Loader,
  Paper,
  Stack,
  Table,
  Text,
  Tooltip,
  useMantineColorScheme,
} from '@mantine/core'
import { mdiCrown } from '@mdi/js'
import { Icon } from '@mdi/react'
import cx from 'clsx'
import { FC } from 'react'
import { useTranslation } from 'react-i18next'
import { useKothScoreboard } from '@Hooks/useGame'
import classes from '@Styles/ScoreboardTable.module.css'

interface KothScoreboardTableProps {
  numId: number
}

const statusColor = (s?: string | null) => {
  switch (s) {
    case 'Ok':
      return 'teal'
    case 'Mumble':
      return 'yellow'
    case 'Offline':
      return 'red'
    case 'InternalError':
      return 'gray'
    default:
      return 'gray'
  }
}

/**
 * King of the Hill — dedicated scoreboard. One column per hill (with the
 * current holder badge in the header), one row per team, ranked by total
 * hold credit. Visually styled to match <see cref="AdScoreboardTable"/>'s
 * jeopardy parity (same CSS module — sticky left pin, table-layout fixed)
 * but stripped to the data KotH actually has (no attack/defense/SLA columns,
 * no captures count).
 */
export const KothScoreboardTable: FC<KothScoreboardTableProps> = ({ numId }) => {
  const { kothScoreboard } = useKothScoreboard(numId)
  const { t } = useTranslation()
  const { colorScheme } = useMantineColorScheme()

  if (!kothScoreboard) {
    return (
      <Center py="xl">
        <Loader />
      </Center>
    )
  }

  const { hills, teams, latestRound } = kothScoreboard

  if (hills.length === 0) {
    return (
      <Center py="xl">
        <Stack gap={4} align="center">
          <Icon path={mdiCrown} size={2} color="var(--mantine-color-violet-6)" />
          <Text fw="bold">{t('game.content.koth.no_hills.title', 'No hills configured')}</Text>
          <Text size="sm" c="dimmed">
            {t('game.content.koth.no_hills.body', 'An organizer needs to enable at least one KotH challenge.')}
          </Text>
        </Stack>
      </Center>
    )
  }

  return (
    <Paper shadow="xs" p={0} radius="sm">
      <Stack gap={0}>
        {/* Header banner with the round counter — mirrors AdScoreboardTable's title row. */}
        <Group justify="space-between" px="md" py="xs" wrap="nowrap"
          style={{ borderBottom: '1px solid var(--mantine-color-default-border)' }}>
          <Group gap="xs">
            <Icon path={mdiCrown} size={0.9} color="var(--mantine-color-violet-6)" />
            <Text fw="bold" size="sm">
              {t('game.content.koth.scoreboard_title', 'King of the Hill — live')}
            </Text>
          </Group>
          <Text size="xs" c="dimmed">
            {t('game.content.koth.round', 'Round {{round}}', { round: latestRound })}
          </Text>
        </Group>

        <Box style={{ overflowX: 'auto' }}>
          <Table
            striped
            withColumnBorders
            highlightOnHover
            classNames={{ table: classes.table }}
            style={{ tableLayout: 'fixed', minWidth: 600 + hills.length * 140 }}
          >
            <Table.Thead>
              <Table.Tr>
                <Table.Th style={{ width: 44, textAlign: 'center' }}>#</Table.Th>
                <Table.Th style={{ width: 200 }}>{t('game.scoreboard.team', 'Team')}</Table.Th>
                <Table.Th style={{ width: 80, textAlign: 'right' }}>
                  {t('game.scoreboard.total', 'Total')}
                </Table.Th>
                {hills.map((h) => (
                  <Table.Th key={h.challengeId} style={{ width: 140, textAlign: 'center' }}>
                    <Stack gap={2} align="center">
                      <Text size="xs" fw="bold" truncate>
                        {h.title}
                      </Text>
                      <Group gap={4} justify="center" wrap="nowrap">
                        <Badge
                          size="xs"
                          variant={h.lastCheckStatus ? 'filled' : 'light'}
                          color={statusColor(h.lastCheckStatus)}
                        >
                          {h.lastCheckStatus ?? '—'}
                        </Badge>
                        {h.currentHolderTeamName && (
                          <Tooltip label={t('game.tooltip.koth.holder',
                            'Currently held by {{team}}', { team: h.currentHolderTeamName })}>
                            <Badge size="xs" variant="light" color="violet"
                              leftSection={<Icon path={mdiCrown} size={0.4} />}>
                              {h.currentHolderTeamName}
                            </Badge>
                          </Tooltip>
                        )}
                      </Group>
                    </Stack>
                  </Table.Th>
                ))}
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {teams.length === 0 ? (
                <Table.Tr>
                  <Table.Td colSpan={3 + hills.length}>
                    <Center py="xl">
                      <Text c="dimmed">
                        {t('game.content.koth.no_teams', 'No accepted teams yet.')}
                      </Text>
                    </Center>
                  </Table.Td>
                </Table.Tr>
              ) : teams.map((team) => (
                <Table.Tr key={team.participationId}>
                  <Table.Td ta="center" fw="bold">{team.rank}</Table.Td>
                  <Table.Td>
                    <Stack gap={0}>
                      <Text size="sm" fw="bold" truncate>{team.teamName}</Text>
                      {team.division && (
                        <Text size="xs" c="dimmed" truncate>{team.division}</Text>
                      )}
                    </Stack>
                  </Table.Td>
                  <Table.Td ta="right" fw="bold">
                    {team.total.toFixed(1)}
                  </Table.Td>
                  {hills.map((h) => {
                    const cell = team.hills.find((x) => x.challengeId === h.challengeId)
                    const pts = cell?.points ?? 0
                    const ticks = cell?.ticksHeld ?? 0
                    return (
                      <Table.Td
                        key={h.challengeId}
                        ta="center"
                        className={cx(cell?.isCurrentHolder && classes.kothHolder)}
                        style={cell?.isCurrentHolder ? {
                          background: colorScheme === 'dark'
                            ? 'var(--mantine-color-violet-9)'
                            : 'var(--mantine-color-violet-1)',
                        } : undefined}
                      >
                        <Stack gap={0}>
                          <Text size="sm" fw={cell?.isCurrentHolder ? 'bold' : undefined}>
                            {pts.toFixed(1)}
                          </Text>
                          {ticks > 0 && (
                            <Text size="xs" c="dimmed">
                              {t('game.content.koth.ticks', '{{n}} ticks', { n: ticks })}
                            </Text>
                          )}
                        </Stack>
                      </Table.Td>
                    )
                  })}
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
        </Box>
      </Stack>
    </Paper>
  )
}
